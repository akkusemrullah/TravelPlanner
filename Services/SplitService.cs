using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public class SplitService : ISplitService
    {
        private readonly IDatabaseService _databaseService;

        public SplitService(IDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public async Task<List<Participant>> GetParticipantsAsync(int tripId)
        {
            var db = await _databaseService.Connection;
            return await db.Table<Participant>()
                .Where(p => p.TripId == tripId)
                .OrderBy(p => p.CreatedDate)
                .ToListAsync();
        }

        public async Task<Participant> SaveParticipantAsync(int tripId, string name, int participantId = 0)
        {
            var clean = (name ?? string.Empty).Trim();
            if (clean.Length == 0)
                throw new ArgumentException("Name is required.", nameof(name));
            if (clean.Length > 60)
                clean = clean[..60];
            clean = Helpers.TextCase.CapitalizeWords(clean) ?? clean; // "ali veli" -> "Ali Veli"

            var existing = await GetParticipantsAsync(tripId);
            if (existing.Any(p => p.Id != participantId && string.Equals(p.Name, clean, StringComparison.CurrentCultureIgnoreCase)))
                throw new DuplicateParticipantException(clean);

            var db = await _databaseService.Connection;
            var participant = existing.FirstOrDefault(p => p.Id == participantId) ?? new Participant { TripId = tripId };
            participant.Name = clean;

            await db.SaveTrackedAsync(participant);

            return participant;
        }

        public async Task<int> GetUsageCountAsync(int participantId)
        {
            var db = await _databaseService.Connection;
            return await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Expense WHERE PaidByParticipantId = ? OR Id IN (SELECT ExpenseId FROM ExpenseShare WHERE ParticipantId = ?)",
                participantId, participantId);
        }

        public async Task DeleteParticipantAsync(Participant participant)
        {
            var db = await _databaseService.Connection;
            var participantId = participant.Id;
            await db.RunInTransactionAsync(conn =>
            {
                var batch = TrashBatch.For(participant);
                conn.DeleteAllTracked<ExpenseShare>(s => s.ParticipantId == participantId, batch);

                foreach (var expense in conn.Table<Expense>().Where(e => e.PaidByParticipantId == participantId).ToList())
                {
                    expense.PaidByParticipantId = null;
                    conn.UpdateTracked(expense);
                }

                conn.DeleteTracked(participant, batch);
            });
        }

        public async Task<List<int>> GetShareParticipantIdsAsync(int expenseId)
        {
            var db = await _databaseService.Connection;
            var shares = await db.Table<ExpenseShare>().Where(s => s.ExpenseId == expenseId).ToListAsync();
            return shares.Select(s => s.ParticipantId).Distinct().ToList();
        }

        public async Task SaveSplitAsync(Expense expense, int? paidByParticipantId, IReadOnlyCollection<int> participantIds)
        {
            var db = await _databaseService.Connection;
            var expenseId = expense.Id;
            var ids = participantIds.Distinct().ToList();

            await db.RunInTransactionAsync(conn =>
            {
                if (conn.Find<Expense>(expenseId) is { } stored && stored.PaidByParticipantId != paidByParticipantId)
                {
                    stored.PaidByParticipantId = paidByParticipantId;
                    conn.UpdateTracked(stored);
                }

                // Only the difference is written (and synced): shares that stay are left alone.
                var current = conn.Table<ExpenseShare>().Where(s => s.ExpenseId == expenseId).ToList();
                var batch = TrashBatch.For<Expense>(expenseId);
                foreach (var share in current.Where(s => !ids.Contains(s.ParticipantId)))
                    conn.DeleteTracked(share, batch);

                // Duplicates (shouldn't exist) are cleaned up too: one share per participant.
                foreach (var duplicate in current.Where(s => ids.Contains(s.ParticipantId))
                                                 .GroupBy(s => s.ParticipantId)
                                                 .SelectMany(g => g.Skip(1)))
                    conn.DeleteTracked(duplicate, batch);

                foreach (var id in ids.Where(id => current.All(s => s.ParticipantId != id)))
                    conn.InsertTracked(new ExpenseShare { ExpenseId = expenseId, ParticipantId = id });
            });

            expense.PaidByParticipantId = paidByParticipantId;
        }

        public async Task<int> AddMembersAsParticipantsAsync(int tripId, IReadOnlyList<TripMember> members)
        {
            var db = await _databaseService.Connection;
            var changed = 0;

            await db.RunInTransactionAsync(conn =>
            {
                var participants = conn.Table<Participant>().Where(p => p.TripId == tripId).ToList();
                foreach (var member in members)
                {
                    if (participants.Any(p => string.Equals(p.UserId, member.UserId, StringComparison.OrdinalIgnoreCase)))
                        continue; // already linked

                    var name = ParticipantNameFor(member);
                    var sameName = participants.FirstOrDefault(p =>
                        string.IsNullOrEmpty(p.UserId) && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
                    if (sameName is not null)
                    {
                        sameName.UserId = member.UserId;
                        conn.UpdateTracked(sameName);
                        changed++;
                        continue;
                    }

                    // Names stay unique per trip: "Ali", "Ali (2)" …
                    var unique = name;
                    for (var n = 2; participants.Any(p => string.Equals(p.Name, unique, StringComparison.CurrentCultureIgnoreCase)); n++)
                        unique = Helpers.InputLimits.Truncate(name, 54) + $" ({n})";

                    var participant = new Participant { TripId = tripId, Name = unique, UserId = member.UserId };
                    conn.InsertTracked(participant);
                    participants.Add(participant);
                    changed++;
                }
            });

            return changed;
        }

        /// <summary>The member's name, or the part of the e-mail before "@", capitalised and within the column limit.</summary>
        internal static string ParticipantNameFor(TripMember member)
        {
            var name = !string.IsNullOrWhiteSpace(member.DisplayName)
                ? member.DisplayName!.Trim()
                : (member.Email ?? "?").Split('@')[0].Trim();
            if (name.Length == 0)
                name = "?";
            name = Helpers.InputLimits.Truncate(name, 60)!;
            return Helpers.TextCase.CapitalizeWords(name) ?? name;
        }

        public async Task<SplitSummary> GetSummaryAsync(int tripId)
        {
            var db = await _databaseService.Connection;
            var participants = await GetParticipantsAsync(tripId);
            var expenses = await db.Table<Expense>().Where(e => e.TripId == tripId).ToListAsync();
            var shares = await db.QueryAsync<ExpenseShare>(
                "SELECT s.* FROM ExpenseShare s INNER JOIN Expense e ON e.Id = s.ExpenseId WHERE e.TripId = ?", tripId);

            return SplitCalculator.Summarize(participants, expenses, shares);
        }
    }

    /// <summary>Pure split arithmetic (TP-504), unit-tested directly.</summary>
    public static class SplitCalculator
    {
        /// <summary>
        /// Splits <paramref name="totalMinor"/> into <paramref name="count"/> equal parts; the leftover kuruş go
        /// one each to the first parts, so the parts always add up exactly to the total.
        /// </summary>
        public static long[] EqualShares(long totalMinor, int count)
        {
            if (count <= 0)
                return Array.Empty<long>();

            var baseShare = totalMinor / count;
            var remainder = totalMinor % count;
            return Enumerable.Range(0, count).Select(i => baseShare + (i < remainder ? 1 : 0)).ToArray();
        }

        public static SplitSummary Summarize(
            IReadOnlyList<Participant> participants,
            IReadOnlyList<Expense> expenses,
            IReadOnlyList<ExpenseShare> shares)
        {
            var byId = participants.ToDictionary(p => p.Id);
            var paid = participants.ToDictionary(p => p.Id, _ => 0L);
            var owed = participants.ToDictionary(p => p.Id, _ => 0L);
            var sharesByExpense = shares
                .Where(s => byId.ContainsKey(s.ParticipantId))
                .GroupBy(s => s.ExpenseId)
                .ToDictionary(g => g.Key, g => g.Select(s => s.ParticipantId).Distinct().OrderBy(id => id).ToList());

            long splitTotal = 0;
            var unsplit = 0;

            foreach (var expense in expenses)
            {
                if (expense.PaidByParticipantId is not int payer || !byId.ContainsKey(payer) ||
                    !sharesByExpense.TryGetValue(expense.Id, out var members) || members.Count == 0)
                {
                    unsplit++;
                    continue;
                }

                paid[payer] += expense.AmountMinor;
                splitTotal += expense.AmountMinor;

                var parts = EqualShares(expense.AmountMinor, members.Count);
                for (var i = 0; i < members.Count; i++)
                    owed[members[i]] += parts[i];
            }

            var balances = participants.Select(p => new ParticipantBalance(p, paid[p.Id], owed[p.Id])).ToList();
            return new SplitSummary(balances, Settle(balances), splitTotal, unsplit);
        }

        /// <summary>
        /// Greedy settlement: the biggest debtor pays the biggest creditor until one is square, repeated. Gives at
        /// most n-1 transfers, which is what people expect from a "who owes whom" list.
        /// </summary>
        public static List<Settlement> Settle(IReadOnlyList<ParticipantBalance> balances)
        {
            var debtors = balances.Where(b => b.NetMinor < 0).Select(b => (b.Participant, Amount: -b.NetMinor)).ToList();
            var creditors = balances.Where(b => b.NetMinor > 0).Select(b => (b.Participant, Amount: b.NetMinor)).ToList();
            var settlements = new List<Settlement>();

            while (debtors.Count > 0 && creditors.Count > 0)
            {
                debtors = debtors.OrderByDescending(d => d.Amount).ThenBy(d => d.Participant.Id).ToList();
                creditors = creditors.OrderByDescending(c => c.Amount).ThenBy(c => c.Participant.Id).ToList();

                var debtor = debtors[0];
                var creditor = creditors[0];
                var amount = Math.Min(debtor.Amount, creditor.Amount);

                settlements.Add(new Settlement(debtor.Participant, creditor.Participant, amount));

                debtors[0] = (debtor.Participant, debtor.Amount - amount);
                creditors[0] = (creditor.Participant, creditor.Amount - amount);
                debtors.RemoveAll(d => d.Amount == 0);
                creditors.RemoveAll(c => c.Amount == 0);
            }

            return settlements;
        }
    }

    public sealed class DuplicateParticipantException : Exception
    {
        public DuplicateParticipantException(string name) : base($"Participant '{name}' already exists.")
        {
            Name = name;
        }

        public string Name { get; }
    }
}
