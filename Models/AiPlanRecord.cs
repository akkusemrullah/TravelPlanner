using SQLite;

namespace TravelPlanner.Models
{
    /// <summary>
    /// TP-108: an AI plan that got ready, kept on the phone so it can be opened again after "Soruları değiştir" or
    /// another plan (no plan used). Tied to the trip by its SyncId, so a trip restored from the trash keeps its plans;
    /// plans of a trip that is gone (and not in the trash) are removed. Per account database, never synced.
    /// </summary>
    [Table("AiPlanRecord")]
    public class AiPlanRecord
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed, System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string TripSyncId { get; set; } = string.Empty;

        /// <summary>The background job's id: the same plan is saved once.</summary>
        [Indexed, System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string JobId { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; }

        /// <summary>The <see cref="AiPlanResult"/> as JSON.</summary>
        public string ResultJson { get; set; } = string.Empty;

        /// <summary>The <see cref="AiPlanAnswers"/> as JSON, or null (a plan made before TP-108).</summary>
        public string? AnswersJson { get; set; }

        /// <summary>When something of it was added to the trip.</summary>
        public DateTime? AppliedUtc { get; set; }
    }

    /// <summary>TP-108: the answers a plan was made with, for its row in "Önceki planlar".</summary>
    public sealed record AiPlanAnswers(IReadOnlyList<string> Interests, string Pace, string Budget, string Companions)
    {
        public static AiPlanAnswers From(AiPlanRequest request) =>
            new(request.Interests.ToList(), request.Pace, request.Budget, request.Companions);
    }
}
