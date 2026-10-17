using System.Globalization;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Helpers
{
    /// <summary>One titled part of a long text (privacy policy, KVKK notice …). <see cref="Heading"/> is null for the intro.</summary>
    public sealed record DocumentSection(string? Heading, string Body);

    /// <summary>One question of the help centre with its answer.</summary>
    public sealed record HelpQuestion(string Question, string Answer);

    /// <summary>A group of help-centre questions ("Hesap ve eşitleme" …).</summary>
    public sealed record HelpCategory(string Title, IReadOnlyList<HelpQuestion> Questions);

    /// <summary>
    /// The long texts of the app (legal documents, help centre) are kept as single localized strings with a tiny
    /// markup, so translators see whole documents:
    /// <list type="bullet">
    /// <item><c>## Heading</c> starts a section (a help-centre category);</item>
    /// <item><c>? Question</c> starts a help-centre question - the lines below it are the answer;</item>
    /// <item>every other line is body text (blank lines separate paragraphs, "• " lines are bullets);</item>
    /// <item><c>{email}</c>, <c>{controller}</c>, <c>{address}</c>, <c>{phone}</c>, <c>{publisher}</c>, <c>{updated}</c>
    /// are filled from <see cref="AppConstants"/>.</item>
    /// </list>
    /// </summary>
    public static class DocumentText
    {
        /// <summary>Date shown as "last updated" on every legal document. Bump it whenever a text changes.</summary>
        public static readonly DateTime LastUpdated = new(2026, 10, 18);

        public static string Fill(string text, CultureInfo? culture = null) => text
            .Replace("{email}", AppConstants.SupportEmail, StringComparison.Ordinal)
            .Replace("{controller}", AppConstants.DataControllerName, StringComparison.Ordinal)
            .Replace("{address}", AppConstants.DataControllerAddress, StringComparison.Ordinal)
            .Replace("{phone}", AppConstants.DataControllerPhone, StringComparison.Ordinal)
            .Replace("{publisher}", AppConstants.PublisherName, StringComparison.Ordinal)
            .Replace("{updated}", LastUpdated.ToString("d MMMM yyyy", culture ?? CultureInfo.CurrentCulture), StringComparison.Ordinal);

        /// <summary>Splits a document into its sections (text before the first heading becomes a heading-less intro).</summary>
        public static IReadOnlyList<DocumentSection> ParseSections(string? text)
        {
            var sections = new List<DocumentSection>();
            string? heading = null;
            var body = new List<string>();

            void Flush()
            {
                var joined = JoinBody(body);
                if (heading is not null || joined.Length > 0)
                    sections.Add(new DocumentSection(heading, joined));
                body.Clear();
            }

            foreach (var raw in Lines(text))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    Flush();
                    heading = line[3..].Trim();
                }
                else
                {
                    body.Add(line);
                }
            }

            Flush();
            return sections;
        }

        /// <summary>Reads the help centre: <c>## Category</c>, then <c>? Question</c> lines each followed by the answer.</summary>
        public static IReadOnlyList<HelpCategory> ParseHelp(string? text)
        {
            var categories = new List<HelpCategory>();
            string? category = null;
            var questions = new List<HelpQuestion>();
            string? question = null;
            var answer = new List<string>();

            void FlushQuestion()
            {
                if (question is not null)
                    questions.Add(new HelpQuestion(question, JoinBody(answer)));
                question = null;
                answer.Clear();
            }

            void FlushCategory()
            {
                FlushQuestion();
                if (category is not null && questions.Count > 0)
                    categories.Add(new HelpCategory(category, questions.ToList()));
                questions.Clear();
            }

            foreach (var raw in Lines(text))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    FlushCategory();
                    category = line[3..].Trim();
                }
                else if (line.StartsWith("? ", StringComparison.Ordinal))
                {
                    FlushQuestion();
                    question = line[2..].Trim();
                }
                else if (question is not null)
                {
                    answer.Add(line);
                }
            }

            FlushCategory();
            return categories;
        }

        private static IEnumerable<string> Lines(string? text) =>
            (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        /// <summary>Body lines → text: outer blank lines dropped, runs of blank lines collapsed to one.</summary>
        private static string JoinBody(List<string> lines)
        {
            var kept = new List<string>();
            foreach (var line in lines)
            {
                if (line.Length == 0 && (kept.Count == 0 || kept[^1].Length == 0))
                    continue;
                kept.Add(line);
            }

            while (kept.Count > 0 && kept[^1].Length == 0)
                kept.RemoveAt(kept.Count - 1);
            return string.Join("\n", kept);
        }
    }

    /// <summary>The legal documents shown in the app (Profil → Yasal ve gizlilik).</summary>
    public enum LegalDocumentKind
    {
        Privacy,
        Kvkk,
        Terms,
        Licenses,
    }

    public static class LegalDocuments
    {
        /// <summary>Query value of each document (route <see cref="Routes.Privacy"/>?doc=…).</summary>
        public const string PrivacyKey = "privacy";
        public const string KvkkKey = "kvkk";
        public const string TermsKey = "terms";
        public const string LicensesKey = "licenses";

        /// <summary>The document a query value names; anything unknown shows the privacy policy.</summary>
        public static LegalDocumentKind FromKey(string? key) => key?.Trim().ToLowerInvariant() switch
        {
            KvkkKey => LegalDocumentKind.Kvkk,
            TermsKey => LegalDocumentKind.Terms,
            LicensesKey => LegalDocumentKind.Licenses,
            _ => LegalDocumentKind.Privacy,
        };

        public static string Key(LegalDocumentKind kind) => kind switch
        {
            LegalDocumentKind.Kvkk => KvkkKey,
            LegalDocumentKind.Terms => TermsKey,
            LegalDocumentKind.Licenses => LicensesKey,
            _ => PrivacyKey,
        };

        public static string Title(LegalDocumentKind kind) => kind switch
        {
            LegalDocumentKind.Kvkk => AppResources.Kvkk_Title,
            LegalDocumentKind.Terms => AppResources.Terms_Title,
            LegalDocumentKind.Licenses => AppResources.Licenses_Title,
            _ => AppResources.Privacy_Title,
        };

        /// <summary>The document's text with the contact details filled in.</summary>
        public static string Body(LegalDocumentKind kind) => DocumentText.Fill(kind switch
        {
            LegalDocumentKind.Kvkk => AppResources.Kvkk_Body,
            LegalDocumentKind.Terms => AppResources.Terms_Body,
            LegalDocumentKind.Licenses => AppResources.Licenses_Body,
            _ => AppResources.Privacy_Body,
        });

        public static IReadOnlyList<DocumentSection> Sections(LegalDocumentKind kind) => DocumentText.ParseSections(Body(kind));
    }
}
