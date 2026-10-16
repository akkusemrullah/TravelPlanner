using System.Globalization;
using System.Text;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services
{
    /// <summary>
    /// One piece of the trip's plan the smart packing list reads: an itinerary activity, a saved place or the
    /// trip itself. <paramref name="Label"/> is what the suggestion's reason quotes ("Planda: “Kaputaş Plajı”").
    /// </summary>
    public sealed record PackingPlanEntry(string Label, string Text);

    /// <summary>
    /// Plan-aware packing rules: words in the plan ("plaj", "yürüyüş", "toplantı", "cami"…) bring the items that
    /// activity needs. Pure and deterministic. Matching ignores case and Turkish characters ("DAG YURUYUSU" works)
    /// and is word-based, so "Denizli" doesn't count as the sea and "Galata" isn't a gala.
    /// </summary>
    public static class PackingPlanMatcher
    {
        /// <summary>The longest plan text quoted in a reason.</summary>
        public const int MaxLabelLength = 40;

        /// <summary>
        /// A rule fires when any keyword matches. Keywords are folded (lower case, no Turkish characters);
        /// a word ending in '*' matches as a prefix (Turkish suffixes: "plaja", "plajda"), otherwise exactly;
        /// several words must appear in a row ("milli park*"). Words starting with an exclusion never match.
        /// </summary>
        private sealed record Rule(string[] Keywords, string[] Exclusions, Func<string>[] Items);

        private static readonly Rule[] Rules =
        {
            // Sea, beach, pool
            new(new[] { "plaj*", "sahil*", "deniz*", "beach*", "sea", "seaside", "havuz*", "pool*", "snorkel*", "dalis*", "diving", "dive",
                        "aquapark*", "su park*" },
                new[] { "denizli" },
                new Func<string>[] { () => AppResources.Packing_Swimwear, () => AppResources.Packing_BeachTowel, () => AppResources.Packing_FlipFlops }),

            // Turkish bath, hot springs, spa
            new(new[] { "hamam*", "kaplica*", "termal*", "thermal*", "spa", "sauna*" },
                Array.Empty<string>(),
                new Func<string>[] { () => AppResources.Packing_Swimwear, () => AppResources.Packing_FlipFlops }),

            // Boat trips and ferries
            new(new[] { "tekne*", "gulet*", "yat", "feribot*", "ferry*", "cruise*", "boat*", "mavi tur*" },
                Array.Empty<string>(),
                new Func<string>[] { () => AppResources.Packing_SeasicknessPills }),

            // Hiking and nature
            new(new[] { "yuruyus*", "trek*", "hike", "hikes", "hiking", "tirmanis*", "kanyon*", "canyon*", "selale*", "waterfall*",
                        "orman*", "forest*", "milli park*", "national park*", "dag*", "mountain*", "yayla*", "doga*" },
                new[] { "dagit", "dagil", "dagin", "dogal", "dogan", "dogum" },
                new Func<string>[] { () => AppResources.Packing_HikingShoes, () => AppResources.Packing_WaterBottle, () => AppResources.Packing_Daypack }),

            // Camping
            new(new[] { "kamp*", "camp*", "cadir*", "glamping*" },
                new[] { "kampus", "kampanya", "campus", "campaign", "campo" },
                new Func<string>[] { () => AppResources.Packing_SleepingBag, () => AppResources.Packing_Flashlight }),

            // Skiing (incl. Türkiye's ski resorts)
            new(new[] { "kayak*", "ski", "skiing", "snowboard*", "uludag*", "palandoken*", "kartalkaya*", "erciyes*", "sarikamis*" },
                Array.Empty<string>(),
                new Func<string>[] { () => AppResources.Packing_SkiWear, () => AppResources.Packing_SkiGoggles,
                                     () => AppResources.Packing_GlovesHat, () => AppResources.Packing_ThermalWear }),

            // Business
            new(new[] { "toplanti*", "konferans*", "kongre*", "fuar*", "sunum*", "seminer*", "meeting*", "conference*", "congress*",
                        "seminar*", "workshop*", "business*", "is gezi*", "is seyahat*", "is gorusme*" },
                Array.Empty<string>(),
                new Func<string>[] { () => AppResources.Packing_FormalWear, () => AppResources.Packing_Laptop, () => AppResources.Packing_BusinessCards }),

            // Places of worship (dress code)
            new(new[] { "cami*", "mosque*", "kilise*", "church*", "katedral*", "cathedral*", "tapinak*", "temple*", "sinagog*",
                        "synagogue*", "vatikan*", "vatican*", "turbe*", "manastir*", "monastery*" },
                new[] { "camino" },
                new Func<string>[] { () => AppResources.Packing_ModestClothes, () => AppResources.Packing_Scarf }),

            // Formal events
            new(new[] { "dugun*", "nikah*", "wedding*", "gala", "opera", "balo*" },
                Array.Empty<string>(),
                new Func<string>[] { () => AppResources.Packing_SmartOutfit }),

            // Flights
            new(new[] { "ucus*", "ucak*", "havalimani*", "havaalani*", "flight*", "airport*" },
                Array.Empty<string>(),
                new Func<string>[] { () => AppResources.Packing_NeckPillow, () => AppResources.Packing_Headphones }),
        };

        /// <summary>
        /// Items the plan calls for, each with the plan entry that triggered it, in rule order. The same item can
        /// come from several rules; callers drop duplicates.
        /// </summary>
        public static IEnumerable<(string Title, string Reason)> Match(IEnumerable<PackingPlanEntry> plan)
        {
            var entries = plan
                .Where(e => !string.IsNullOrWhiteSpace(e.Text))
                .Select(e => (Entry: e, Tokens: Tokenize(e.Text)))
                .ToList();
            if (entries.Count == 0)
                yield break;

            foreach (var rule in Rules)
            {
                var hit = entries.FirstOrDefault(e => Matches(rule, e.Tokens));
                if (hit.Entry is null)
                    continue;

                var reason = string.Format(CultureInfo.CurrentCulture, AppResources.Packing_ReasonPlanFormat, Shorten(hit.Entry.Label));
                foreach (var item in rule.Items)
                    yield return (item(), reason);
            }
        }

        /// <summary>Lower case, Turkish characters and accents folded ("Dağ Yürüyüşü" → "dag yuruyusu"), punctuation as spaces.</summary>
        internal static string Fold(string text)
        {
            var mapped = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                mapped.Append(ch switch
                {
                    'İ' or 'I' or 'ı' => 'i',
                    'Ş' or 'ş' => 's',
                    'Ğ' or 'ğ' => 'g',
                    'Ç' or 'ç' => 'c',
                    'Ö' or 'ö' => 'o',
                    'Ü' or 'ü' => 'u',
                    _ => ch,
                });
            }

            var folded = new StringBuilder(mapped.Length);
            foreach (var ch in mapped.ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue;
                folded.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
            }

            return folded.ToString();
        }

        private static string[] Tokenize(string text) =>
            Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        private static bool Matches(Rule rule, string[] tokens)
        {
            foreach (var keyword in rule.Keywords)
            {
                var words = keyword.Split(' ');
                for (var i = 0; i + words.Length <= tokens.Length; i++)
                {
                    var all = true;
                    for (var j = 0; j < words.Length && all; j++)
                        all = WordMatches(words[j], tokens[i + j], rule.Exclusions);
                    if (all)
                        return true;
                }
            }

            return false;
        }

        private static bool WordMatches(string word, string token, string[] exclusions)
        {
            if (exclusions.Any(x => token.StartsWith(x, StringComparison.Ordinal)))
                return false;

            return word.EndsWith('*')
                ? token.StartsWith(word[..^1], StringComparison.Ordinal)
                : token == word;
        }

        private static string Shorten(string label)
        {
            var clean = label.Trim();
            return clean.Length <= MaxLabelLength ? clean : clean[..(MaxLabelLength - 1)].TrimEnd() + "…";
        }
    }
}
