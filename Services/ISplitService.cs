using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>One participant's position in a trip's cost split (minor units of the trip currency).</summary>
    public sealed record ParticipantBalance(Participant Participant, long PaidMinor, long ShareMinor)
    {
        /// <summary>Positive = is owed money, negative = owes money.</summary>
        public long NetMinor => PaidMinor - ShareMinor;
    }

    /// <summary>"<see cref="From"/> pays <see cref="To"/> <see cref="AmountMinor"/>".</summary>
    public sealed record Settlement(Participant From, Participant To, long AmountMinor);

    /// <summary>Everything the split summary page shows (TP-504).</summary>
    public sealed record SplitSummary(
        IReadOnlyList<ParticipantBalance> Balances,
        IReadOnlyList<Settlement> Settlements,
        long SplitTotalMinor,
        int UnsplitExpenseCount);

    /// <summary>Trip participants and expense splitting (TP-504). Implemented by SplitService.</summary>
    public interface ISplitService
    {
        Task<List<Participant>> GetParticipantsAsync(int tripId);

        /// <summary>Adds or renames a participant. Names are unique per trip (case-insensitive).</summary>
        Task<Participant> SaveParticipantAsync(int tripId, string name, int participantId = 0);

        /// <summary>How many expenses a participant paid or shares in (shown before deleting them).</summary>
        Task<int> GetUsageCountAsync(int participantId);

        /// <summary>Deletes a participant, their shares, and clears them as payer - one transaction.</summary>
        Task DeleteParticipantAsync(Participant participant);

        /// <summary>Participant ids an expense is split between.</summary>
        Task<List<int>> GetShareParticipantIdsAsync(int expenseId);

        /// <summary>Replaces an expense's payer and split participants (one transaction).</summary>
        Task SaveSplitAsync(Expense expense, int? paidByParticipantId, IReadOnlyCollection<int> participantIds);

        /// <summary>Balances and the fewest "who pays whom" transfers that settle them.</summary>
        Task<SplitSummary> GetSummaryAsync(int tripId);

        /// <summary>
        /// G6: makes every member of a shared trip a participant linked to their account. A participant with the
        /// same name and no account yet is linked instead of duplicated. Returns how many were added or linked.
        /// </summary>
        Task<int> AddMembersAsParticipantsAsync(int tripId, IReadOnlyList<TripMember> members);
    }
}
