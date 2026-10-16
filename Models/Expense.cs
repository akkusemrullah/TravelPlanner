using SQLite;
using System.ComponentModel.DataAnnotations;
using TravelPlanner.Helpers;

namespace TravelPlanner.Models
{
    public class Expense : SyncEntityBase, ISyncEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int TripId { get; set; }

        public ExpenseCategory Category { get; set; }

        /// <summary>
        /// Stored value: the amount in minor units (kuruş/cent) of the TRIP's budget currency. sqlite-net maps
        /// decimal to a REAL (floating point) column, which loses precision on money, so the real column is an
        /// integer and <see cref="Amount"/> is a computed view over it (TP-109). Because every expense of a trip
        /// is stored in the same currency, the SQL SUMs used for budget figures stay correct (TP-403).
        /// </summary>
        public long AmountMinor { get; set; }

        [Ignore, Range(0, double.MaxValue)]
        public decimal Amount
        {
            get => MoneyConverter.FromMinor(AmountMinor);
            set => AmountMinor = MoneyConverter.ToMinor(value);
        }

        /// <summary>The amount as the user typed it, in minor units of <see cref="OriginalCurrencyCode"/> (TP-403).</summary>
        public long OriginalAmountMinor { get; set; }

        [Ignore]
        public decimal OriginalAmount
        {
            get => MoneyConverter.FromMinor(OriginalAmountMinor);
            set => OriginalAmountMinor = MoneyConverter.ToMinor(value);
        }

        /// <summary>
        /// Currency the expense was entered in. Null for expenses recorded before multi-currency support -
        /// those were entered in the trip's currency.
        /// </summary>
        [System.ComponentModel.DataAnnotations.MaxLength(3)]
        public string? OriginalCurrencyCode { get; set; }

        /// <summary>Rate used to convert the original amount into the trip currency (1 when no conversion).</summary>
        public double ExchangeRate { get; set; } = 1d;

        public DateTime Date { get; set; } = DateTime.Today;

        /// <summary>Who paid (TP-504). Null = not part of the cost split.</summary>
        public int? PaidByParticipantId { get; set; }

        [System.ComponentModel.DataAnnotations.MaxLength(250)]
        public string? Description { get; set; }

        /// <summary>The owning trip's budget currency - filled in by ExpenseService when loading, not stored.</summary>
        [Ignore]
        public string? TripCurrencyCode { get; set; }

        [Ignore]
        public string FormattedAmount => MoneyFormatter.FormatIn(Amount, TripCurrencyCode);

        /// <summary>True when the expense was entered in a different currency than the trip's.</summary>
        [Ignore]
        public bool IsConverted =>
            !string.IsNullOrWhiteSpace(OriginalCurrencyCode) &&
            !string.Equals(OriginalCurrencyCode, MoneyFormatter.Normalize(TripCurrencyCode), StringComparison.OrdinalIgnoreCase);

        /// <summary>"€50,00" - the amount as entered, shown under the converted amount when they differ.</summary>
        [Ignore]
        public string FormattedOriginalAmount => MoneyFormatter.FormatIn(OriginalAmount, OriginalCurrencyCode);
    }
}
