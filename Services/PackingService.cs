using System.Globalization;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public class PackingService : IPackingService
    {
        private readonly IDatabaseService _databaseService;

        /// <summary>Same length as PackingTemplate.Name's [MaxLength].</summary>
        public const int MaxTemplateNameLength = 80;

        /// <summary>An item becomes a to-do, so it gets the to-do title limit.</summary>
        public const int MaxTemplateItemLength = 150;

        /// <summary>Applying a template adds one to-do per item - keep that list a sane size.</summary>
        public const int MaxTemplateItems = 200;

        public PackingService(IDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public IReadOnlyList<PackingSuggestion> GetSuggestions(Trip trip, IReadOnlyList<DailyForecast>? forecast, IEnumerable<string> existingTitles,
                                                               IEnumerable<PackingPlanEntry>? plan = null) =>
            PackingSuggestionEngine.Suggest(trip, forecast, existingTitles, plan);

        public async Task<IReadOnlyList<PackingPlanEntry>> GetPlanAsync(Trip trip)
        {
            var plan = new List<PackingPlanEntry>
            {
                // The trip itself: "Kayak tatili", a description like "deniz, kum, güneş", or a city such as Uludağ.
                new(string.IsNullOrWhiteSpace(trip.Name) ? trip.City : trip.Name, string.Join(' ', trip.Name, trip.City, trip.Description))
            };

            var db = await _databaseService.Connection;

            var activities = await db.Table<TripActivity>().Where(a => a.TripId == trip.Id).ToListAsync();
            plan.AddRange(activities
                .OrderBy(a => a.Date).ThenBy(a => a.SortOrder)
                .Select(a => new PackingPlanEntry(a.Title, string.Join(' ', a.Title, a.Note))));

            var places = await db.Table<Place>().Where(p => p.TripId == trip.Id).ToListAsync();
            plan.AddRange(places.Select(p => new PackingPlanEntry(p.Name, string.Join(' ', p.Name, p.Description))));

            return plan;
        }

        public async Task<List<PackingTemplate>> GetTemplatesAsync()
        {
            var db = await _databaseService.Connection;
            return await db.Table<PackingTemplate>().OrderBy(t => t.Name).ToListAsync();
        }

        public async Task<PackingTemplate> SaveTemplateAsync(string name, IEnumerable<string> items)
        {
            var cleanName = Helpers.TextCase.CapitalizeFirst(Helpers.InputLimits.Truncate((name ?? string.Empty).Trim(), MaxTemplateNameLength))!;
            if (cleanName.Length == 0)
                throw new ArgumentException("Template name is required.", nameof(name));

            var cleanItems = items
                .Select(i => i?.Trim() ?? string.Empty)
                .Where(i => i.Length > 0)
                .Select(i => Helpers.TextCase.CapitalizeFirst(Helpers.InputLimits.Truncate(i, MaxTemplateItemLength))!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxTemplateItems)
                .ToList();

            var db = await _databaseService.Connection;
            var all = await db.Table<PackingTemplate>().ToListAsync();
            var template = all.FirstOrDefault(t => string.Equals(t.Name, cleanName, StringComparison.CurrentCultureIgnoreCase))
                           ?? new PackingTemplate { Name = cleanName };

            template.Items = string.Join("\n", cleanItems);

            await db.SaveTrackedAsync(template);

            return template;
        }

        public async Task<int> DeleteTemplateAsync(PackingTemplate template)
        {
            var db = await _databaseService.Connection;
            return await db.DeleteTrackedAsync(template);
        }
    }

    /// <summary>
    /// The rules behind the smart packing list (TP-405). Pure and deterministic, so it's unit-tested directly.
    /// Every item has to make sense for this trip: a passport only when leaving Türkiye (and not where the ID card
    /// is enough), no clothes for a day trip, swimwear from the weather alone only as an unticked "maybe".
    /// Thresholds are deliberately simple and conservative: better to suggest an umbrella that stays in the
    /// bag than to miss one.
    /// </summary>
    public static class PackingSuggestionEngine
    {
        public const int RainProbabilityThreshold = 40;   // %
        public const double RainAmountThreshold = 1.0;    // mm
        public const double CoatBelow = 12.0;             // °C (daily minimum)
        public const double WinterGearBelow = 2.0;        // °C (daily minimum)
        public const double SunGearAbove = 25.0;          // °C (daily maximum)
        public const double SwimwearAbove = 28.0;         // °C (daily maximum)
        public const int LongTripDays = 7;

        /// <summary>The home currency: no "cash in …" suggestion for it.</summary>
        public const string HomeCurrency = "TRY";

        private static readonly HashSet<string> HomeCountries = Folded("Türkiye", "Turkey", "Turkiye", "TR", "TUR");

        // Any of these words in the country text means Türkiye: "Türkiye Cumhuriyeti", "Republic of Türkiye",
        // "Turkey (Türkiye)", or the name in another language from a geocoder (Türkei, Turquie, Turchia…).
        private static readonly HashSet<string> HomeCountryWords = Folded(
            "Türkiye", "Turkiye", "Turkey", "Türkei", "Turkei", "Turquie", "Turquía", "Turchia", "Turcja", "Turkije", "Turkiet", "Tyrkiet");

        // Türkiye's 81 provinces: a trip to one of these with an unrecognised country text is still domestic.
        private static readonly HashSet<string> HomeProvinces = Folded(
            "Adana", "Adıyaman", "Afyonkarahisar", "Afyon", "Ağrı", "Aksaray", "Amasya", "Ankara", "Antalya", "Ardahan", "Artvin", "Aydın",
            "Balıkesir", "Bartın", "Batman", "Bayburt", "Bilecik", "Bingöl", "Bitlis", "Bolu", "Burdur", "Bursa", "Çanakkale", "Çankırı",
            "Çorum", "Denizli", "Diyarbakır", "Düzce", "Edirne", "Elazığ", "Erzincan", "Erzurum", "Eskişehir", "Gaziantep", "Giresun",
            "Gümüşhane", "Hakkari", "Hatay", "Iğdır", "Isparta", "İstanbul", "İzmir", "Kahramanmaraş", "Karabük", "Karaman", "Kars",
            "Kastamonu", "Kayseri", "Kırıkkale", "Kırklareli", "Kırşehir", "Kilis", "Kocaeli", "İzmit", "Konya", "Kütahya", "Malatya",
            "Manisa", "Mardin", "Mersin", "Muğla", "Muş", "Nevşehir", "Niğde", "Ordu", "Osmaniye", "Rize", "Sakarya", "Adapazarı",
            "Samsun", "Siirt", "Sinop", "Sivas", "Şanlıurfa", "Urfa", "Şırnak", "Tekirdağ", "Tokat", "Trabzon", "Tunceli", "Uşak", "Van",
            "Yalova", "Yozgat", "Zonguldak");

        // Destinations whose sockets differ from Türkiye's type C/F (Turkish and English names).
        private static readonly HashSet<string> AdapterCountries = Folded(
            "Birleşik Krallık", "United Kingdom", "İngiltere", "England", "İskoçya", "Scotland",
            "İrlanda", "Ireland", "Malta", "Kıbrıs", "Cyprus", "Kuzey Kıbrıs Türk Cumhuriyeti", "Kuzey Kıbrıs", "KKTC", "Northern Cyprus",
            "Amerika Birleşik Devletleri", "United States", "ABD", "USA", "Kanada", "Canada", "Meksika", "Mexico",
            "Japonya", "Japan", "Avustralya", "Australia", "Yeni Zelanda", "New Zealand",
            "Çin", "China", "Hindistan", "India", "Güney Afrika", "South Africa", "İsviçre", "Switzerland",
            "Brezilya", "Brazil", "İsrail", "Israel", "Birleşik Arap Emirlikleri", "United Arab Emirates",
            "Suudi Arabistan", "Saudi Arabia", "Katar", "Qatar", "Kuveyt", "Kuwait", "Bahreyn", "Bahrain", "Umman", "Oman",
            "Singapur", "Singapore", "Malezya", "Malaysia", "Hong Kong", "Filipinler", "Philippines",
            "Tayvan", "Taiwan", "Kolombiya", "Colombia", "Arjantin", "Argentina", "Jamaika", "Jamaica");

        // Turkish citizens can enter these with the new chip ID card - no passport needed.
        private static readonly HashSet<string> IdCardCountries = Folded(
            "Kuzey Kıbrıs Türk Cumhuriyeti", "Kuzey Kıbrıs", "KKTC", "Northern Cyprus", "Gürcistan", "Georgia");

        // Schengen area: a visa for an ordinary Turkish passport.
        private static readonly HashSet<string> SchengenCountries = Folded(
            "Almanya", "Germany", "Avusturya", "Austria", "Belçika", "Belgium", "Bulgaristan", "Bulgaria",
            "Çekya", "Çek Cumhuriyeti", "Czechia", "Czech Republic", "Danimarka", "Denmark", "Estonya", "Estonia",
            "Finlandiya", "Finland", "Fransa", "France", "Hırvatistan", "Croatia", "Hollanda", "Netherlands",
            "İspanya", "Spain", "İsveç", "Sweden", "İsviçre", "Switzerland", "İtalya", "Italy", "İzlanda", "Iceland",
            "Letonya", "Latvia", "Lihtenştayn", "Liechtenstein", "Litvanya", "Lithuania", "Lüksemburg", "Luxembourg",
            "Macaristan", "Hungary", "Malta", "Norveç", "Norway", "Polonya", "Poland", "Portekiz", "Portugal",
            "Romanya", "Romania", "Slovakya", "Slovakia", "Slovenya", "Slovenia", "Yunanistan", "Greece");

        // Seasons are the other way round here (used only when there is no forecast).
        private static readonly HashSet<string> SouthernCountries = Folded(
            "Avustralya", "Australia", "Yeni Zelanda", "New Zealand", "Arjantin", "Argentina", "Şili", "Chile",
            "Güney Afrika", "South Africa", "Uruguay");

        // WMO snow codes (snow fall, snow grains, snow showers).
        private static readonly HashSet<int> SnowCodes = new() { 71, 73, 75, 77, 85, 86 };

        public static IReadOnlyList<PackingSuggestion> Suggest(Trip trip, IReadOnlyList<DailyForecast>? forecast, IEnumerable<string> existingTitles,
                                                               IEnumerable<PackingPlanEntry>? plan = null)
        {
            var existing = existingTitles.Select(Words).Where(w => w.Length > 0).ToList();
            var suggestions = new List<PackingSuggestion>();

            void Add(string title, string reason, bool selected = true)
            {
                var words = Words(title);
                if (existing.Any(e => SameItem(words, e)))
                    return;
                if (suggestions.Any(s => SameItem(words, Words(s.Title))))
                    return;
                suggestions.Add(new PackingSuggestion(title, reason, selected));
            }

            var days = Math.Max(trip.DurationInDays, 1);
            var dayTrip = days == 1;
            var durationReason = string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ReasonDurationFormat, days);
            var country = trip.Country?.Trim() ?? string.Empty;
            var countryKey = Key(country);
            var abroad = countryKey.Length > 0 && !IsHome(countryKey, Key(trip.City ?? string.Empty));

            // ---- Basics ----
            Add(AppResources.Packing_Charger, AppResources.Packing_ReasonBasic);
            Add(AppResources.Packing_Medicine, AppResources.Packing_ReasonBasic);

            // ---- Length: a day trip needs no change of clothes ----
            if (dayTrip)
            {
                Add(AppResources.Packing_Toiletries, AppResources.Packing_ReasonDayTrip, selected: false);
            }
            else
            {
                Add(AppResources.Packing_Toiletries, AppResources.Packing_ReasonBasic);
                // Nobody packs 60 days of clothes: beyond a week the laundry bag (below) covers the rest.
                var clothesDays = Math.Min(days, LongTripDays);
                Add(string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ClothesFormat, clothesDays), durationReason);
                Add(string.Format(CultureInfo.CurrentCulture, AppResources.Packing_UnderwearFormat, Math.Min(days + 1, LongTripDays + 1)), durationReason);
                Add(AppResources.Packing_Pajamas, durationReason);
            }

            if (days >= 3)
                Add(AppResources.Packing_PowerBank, durationReason);
            if (days > LongTripDays)
                Add(AppResources.Packing_LaundryBag, durationReason);

            // ---- Destination ----
            if (abroad)
            {
                if (IdCardCountries.Contains(countryKey))
                {
                    // KKTC / Georgia: the chip ID card is enough - no passport, no visa.
                    Add(AppResources.Packing_IdCard, string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ReasonIdCardFormat, country));
                }
                else
                {
                    Add(AppResources.Packing_Passport, AppResources.Packing_ReasonAbroad);
                    if (SchengenCountries.Contains(countryKey))
                        Add(AppResources.Packing_SchengenVisa, string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ReasonSchengenFormat, country));
                    else
                        Add(AppResources.Packing_VisaCheck, AppResources.Packing_ReasonAbroad);
                }

                Add(AppResources.Packing_TravelInsurance, AppResources.Packing_ReasonAbroad);
                Add(AppResources.Packing_ExitFee, AppResources.Packing_ReasonExitFee);
                Add(AppResources.Packing_Roaming, AppResources.Packing_ReasonAbroad);
                Add(AppResources.Packing_CardAbroad, AppResources.Packing_ReasonAbroad);

                var currency = trip.CurrencyCode?.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(currency) && currency != HomeCurrency)
                    Add(string.Format(CultureInfo.CurrentCulture, AppResources.Packing_CashFormat, currency), AppResources.Packing_ReasonAbroad);

                if (AdapterCountries.Contains(countryKey))
                    Add(AppResources.Packing_Adapter, string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ReasonPlugFormat, country));
            }
            else
            {
                // Inside Türkiye the ID card is all you need - no passport, visa or travel insurance.
                Add(AppResources.Packing_IdCard, AppResources.Packing_ReasonBasic);
            }

            // ---- Plan: what the itinerary, places and trip details say we'll do ----
            if (plan is not null)
            {
                foreach (var (title, reason) in PackingPlanMatcher.Match(plan))
                    Add(title, reason);
            }

            // ---- Weather ----
            if (forecast is { Count: > 0 })
                AddForWeather(forecast, Add);
            else
                AddForSeason(trip, countryKey, Add);

            return suggestions;
        }

        private static void AddForWeather(IReadOnlyList<DailyForecast> forecast, Action<string, string, bool> add)
        {
            var rainyDays = forecast.Count(d =>
                (d.PrecipitationProbability ?? 0) >= RainProbabilityThreshold ||
                (d.PrecipitationSum ?? 0) >= RainAmountThreshold);

            if (rainyDays > 0)
            {
                var reason = string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ReasonRainFormat, rainyDays);
                add(AppResources.Packing_Umbrella, reason, true);
                add(AppResources.Packing_RainJacket, reason, true);
            }

            var minTemps = forecast.Where(d => d.TemperatureMin is not null).Select(d => d.TemperatureMin!.Value).ToList();
            var maxTemps = forecast.Where(d => d.TemperatureMax is not null).Select(d => d.TemperatureMax!.Value).ToList();

            if (minTemps.Count > 0)
            {
                var coldest = minTemps.Min();
                var coldReason = string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ReasonColdFormat, Math.Round(coldest));

                if (coldest < CoatBelow)
                    add(AppResources.Packing_Coat, coldReason, true);
                if (coldest < WinterGearBelow)
                {
                    add(AppResources.Packing_GlovesHat, coldReason, true);
                    add(AppResources.Packing_ThermalWear, coldReason, true);
                }
            }

            if (forecast.Any(d => SnowCodes.Contains(d.WeatherCode)))
                add(AppResources.Packing_SnowBoots, AppResources.Packing_ReasonSnow, true);

            if (maxTemps.Count > 0)
            {
                var hottest = maxTemps.Max();
                var hotReason = string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ReasonHotFormat, Math.Round(hottest));

                if (hottest >= SunGearAbove)
                {
                    add(AppResources.Packing_Sunscreen, hotReason, true);
                    add(AppResources.Packing_Sunglasses, hotReason, true);
                    add(AppResources.Packing_SunHat, hotReason, true);
                }

                // Heat alone doesn't mean a swim (think Ankara in July): offered unticked. A beach, pool or
                // hamam in the plan has already added it ticked.
                if (hottest >= SwimwearAbove)
                    add(AppResources.Packing_Swimwear, hotReason, false);
            }
        }

        /// <summary>No forecast (offline): a cautious guess from the month, the other way round south of the equator.</summary>
        private static void AddForSeason(Trip trip, string countryKey, Action<string, string, bool> add)
        {
            var middle = trip.StartDate.Date.AddDays(Math.Max(trip.DurationInDays - 1, 0) / 2);
            var month = middle.Month;
            if (SouthernCountries.Contains(countryKey))
                month = (month + 5) % 12 + 1;

            var reason = string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ReasonSeasonFormat,
                                       middle.ToString("MMMM", CultureInfo.CurrentCulture));

            if (month is 12 or 1 or 2)
                add(AppResources.Packing_Coat, reason, true);
            else if (month is 6 or 7 or 8)
            {
                add(AppResources.Packing_Sunglasses, reason, true);
                add(AppResources.Packing_Sunscreen, reason, true);
            }
        }

        /// <summary>
        /// An item counts as already on the list when its words match, or one contains the other as whole words
        /// ("Pasaport" vs "Pasaport ve vize", "Mayo" vs "Mayo / bikini"). Numbers are ignored, so "5 günlük kıyafet"
        /// doesn't come back as "7 günlük kıyafet".
        /// </summary>
        internal static bool SameItem(string[] a, string[] b)
        {
            if (a.Length == 0 || b.Length == 0)
                return false;
            if (a.SequenceEqual(b))
                return true;

            return (string.Join(' ', b).Length >= 4 && ContainsRun(a, b)) ||
                   (string.Join(' ', a).Length >= 4 && ContainsRun(b, a));
        }

        private static bool ContainsRun(string[] haystack, string[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var all = true;
                for (var j = 0; j < needle.Length && all; j++)
                    all = haystack[i + j] == needle[j];
                if (all)
                    return true;
            }

            return false;
        }

        private static string[] Words(string title) =>
            PackingPlanMatcher.Fold(title ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !w.All(char.IsDigit))
                .ToArray();

        /// <summary>
        /// Whether the trip stays in Türkiye: the country is Türkiye however it is written, or it is a country text we
        /// don't recognise at all and the city is one of Türkiye's provinces.
        /// </summary>
        internal static bool IsHome(string countryKey, string cityKey)
        {
            if (HomeCountries.Contains(countryKey) || countryKey.Split(' ').Any(HomeCountryWords.Contains))
                return true;

            var knownForeign = AdapterCountries.Contains(countryKey) || IdCardCountries.Contains(countryKey) ||
                               SchengenCountries.Contains(countryKey) || SouthernCountries.Contains(countryKey);
            return !knownForeign && HomeProvinces.Contains(cityKey);
        }

        /// <summary>"Kuzey Kıbrıs" / "kuzey kibris" / "KUZEY  KIBRIS" → one lookup key.</summary>
        private static string Key(string name) =>
            string.Join(' ', PackingPlanMatcher.Fold(name).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private static HashSet<string> Folded(params string[] names) =>
            new(names.Select(Key), StringComparer.Ordinal);
    }
}
