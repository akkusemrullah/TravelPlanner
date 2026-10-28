using System.Globalization;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// TP-106: one row of Home's "Seyahat takvimin" - a date tile ("09 / KAS") and the trip's card. The next trip's tile
    /// is filled blue, other coming trips are tinted, past ones plain. Pure; <paramref name="today"/> is a seam.
    /// </summary>
    public sealed class HomeTripRow
    {
        /// <summary>Within this many days a coming trip says "12 gün sonra" instead of "Yaklaşan".</summary>
        public const int CountdownDays = 30;

        public HomeTripRow(Trip trip, bool isNext, DateTime? today = null, CultureInfo? culture = null)
        {
            culture ??= CultureInfo.CurrentCulture;
            var now = (today ?? DateTime.Today).Date;
            var start = trip.StartDate.Date;
            var end = trip.EndDate.Date < start ? start : trip.EndDate.Date;

            Id = trip.Id;
            Day = start.ToString("dd", culture);
            Month = TextCase.Upper(start.ToString("MMM", culture).TrimEnd('.'));
            Title = string.IsNullOrWhiteSpace(trip.Name) ? trip.City : trip.Name.Trim();
            var place = string.IsNullOrWhiteSpace(trip.Country) ? trip.City?.Trim() ?? string.Empty
                : string.Equals(trip.City?.Trim(), Title, StringComparison.CurrentCultureIgnoreCase) ? trip.Country.Trim()
                : $"{trip.City?.Trim()}, {trip.Country.Trim()}";
            var days = string.Format(culture, AppResources.Explore_DaysFormat, (end - start).Days + 1);
            Sub = place.Length == 0 ? days : $"{place} · {days}";

            Status = now < start ? TripStatus.Upcoming : now > end ? TripStatus.Completed : TripStatus.Ongoing;
            var until = (start - now).Days;
            (BadgeText, BadgeTone) = Status switch
            {
                TripStatus.Upcoming when until == 1 => (AppResources.Ticket_Tomorrow, "accent"),
                TripStatus.Upcoming when until <= CountdownDays => (string.Format(culture, AppResources.Home_InDaysFormat, until), "accent"),
                TripStatus.Upcoming => (Status.ToDisplayText(), "accent"),
                TripStatus.Ongoing => (Status.ToDisplayText(), "success"),
                _ => (Status.ToDisplayText(), "neutral"),
            };

            IsNext = isNext;
            IsPast = Status == TripStatus.Completed;
        }

        public int Id { get; }
        public string Day { get; }
        public string Month { get; }
        public string Title { get; }
        public string Sub { get; }
        public TripStatus Status { get; }
        public string BadgeText { get; }
        public string BadgeTone { get; }

        /// <summary>The featured trip: its tile is filled blue.</summary>
        public bool IsNext { get; }

        public bool IsPast { get; }

        /// <summary>A coming or ongoing trip that isn't the featured one: tinted tile.</summary>
        public bool IsLater => !IsNext && !IsPast;
    }

    /// <summary>TP-106: a quick link under the Home card ("Plan 5/7", "Görevler 9/14", "Harita").</summary>
    public sealed record HomeChip(string Icon, string Title, string Count, string Route)
    {
        public bool HasCount => Count.Length > 0;

        /// <summary>The Lucide glyph of <see cref="Icon"/> (an Ic.* key of Resources/Styles/Icons.xaml).</summary>
        public string Glyph =>
            Application.Current?.Resources.TryGetValue(Icon, out var value) == true && value is string glyph ? glyph : string.Empty;
    }

    /// <summary>TP-106: a Keşfet sample trip on the empty Home ("Hazır planlarla başla"). No budget, as in Keşfet.</summary>
    public sealed class HomePlanCard
    {
        public HomePlanCard(ExploreTripCard card)
        {
            Id = card.Id;
            Title = card.Title;
            Duration = card.DurationText;
            ThumbUrl = card.ThumbUrl;
            var tags = string.Join(", ", card.TagLabels);
            Sub = tags.Length == 0 ? card.Location : $"{card.Location} · {tags}";
            var firstTag = card.TagLabels.FirstOrDefault();
            ShortSub = firstTag is null ? card.DurationText : $"{card.DurationText} · {firstTag}";
        }

        public Guid Id { get; }
        public string Title { get; }
        public string Duration { get; }
        public string Sub { get; }
        public string ShortSub { get; }
        public string? ThumbUrl { get; }
        public bool HasThumb => ThumbUrl is not null;
    }
}
