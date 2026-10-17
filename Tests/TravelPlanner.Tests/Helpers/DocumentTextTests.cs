using System.Globalization;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>The long in-app texts (legal documents, help centre) and the pages that show them.</summary>
    public class DocumentTextTests
    {
        [Fact]
        public void ParseSections_SplitsAtHeadings_AndKeepsTheIntro()
        {
            var sections = DocumentText.ParseSections("Giriş metni.\n\n## Bir\nbir satır\n\n\n• madde\n\n## İki\niki");

            Assert.Equal(3, sections.Count);
            Assert.Null(sections[0].Heading);
            Assert.Equal("Giriş metni.", sections[0].Body);
            Assert.Equal("Bir", sections[1].Heading);
            Assert.Equal("bir satır\n\n• madde", sections[1].Body); // blank lines collapsed
            Assert.Equal("İki", sections[2].Heading);
        }

        [Fact]
        public void ParseHelp_ReadsCategoriesQuestionsAndAnswers()
        {
            var help = DocumentText.ParseHelp("## A\n? Soru 1\nCevap 1\nikinci satır\n? Soru 2\nCevap 2\n## Boş\n## B\n? Soru 3\nCevap 3");

            Assert.Equal(new[] { "A", "B" }, help.Select(c => c.Title)); // a category without questions is dropped
            Assert.Equal("Cevap 1\nikinci satır", help[0].Questions[0].Answer);
            Assert.Equal("Soru 3", help[1].Questions[0].Question);
        }

        [Fact]
        public void Fill_PutsInTheContactDetails()
        {
            var text = DocumentText.Fill("{controller} – {email} – {address} – {phone} – {publisher}");

            Assert.Equal($"{AppConstants.DataControllerName} – {AppConstants.SupportEmail} – {AppConstants.DataControllerAddress} – " +
                         $"{AppConstants.DataControllerPhone} – {AppConstants.PublisherName}", text);
        }

        [Theory]
        [InlineData(LegalDocumentKind.Privacy)]
        [InlineData(LegalDocumentKind.Kvkk)]
        [InlineData(LegalDocumentKind.Terms)]
        [InlineData(LegalDocumentKind.Licenses)]
        public void EveryLegalDocument_HasSections_AndNoUnfilledPlaceholders(LegalDocumentKind kind)
        {
            var body = LegalDocuments.Body(kind);
            var sections = LegalDocuments.Sections(kind);

            Assert.True(sections.Count >= 4);
            Assert.All(sections.Where(s => s.Heading is not null), s => Assert.False(string.IsNullOrWhiteSpace(s.Body)));
            Assert.DoesNotContain("{", body);
            if (kind != LegalDocumentKind.Licenses)
                Assert.Contains(AppConstants.SupportEmail, body);
        }

        [Fact]
        public void TheKvkkNotice_NamesTheDataController_AndTheRights()
        {
            var body = LegalDocuments.Body(LegalDocumentKind.Kvkk);

            Assert.Contains(AppConstants.DataControllerName, body);
            Assert.Contains(AppConstants.DataControllerAddress, body);
            Assert.Contains("11", body);
        }

        [Theory]
        [InlineData("privacy", LegalDocumentKind.Privacy)]
        [InlineData("KVKK", LegalDocumentKind.Kvkk)]
        [InlineData("terms", LegalDocumentKind.Terms)]
        [InlineData("licenses", LegalDocumentKind.Licenses)]
        [InlineData(null, LegalDocumentKind.Privacy)]
        [InlineData("nonsense", LegalDocumentKind.Privacy)]
        public void DocumentKeys_RoundTrip(string? key, LegalDocumentKind expected)
        {
            Assert.Equal(expected, LegalDocuments.FromKey(key));
            Assert.Equal(expected, LegalDocuments.FromKey(LegalDocuments.Key(expected)));
        }

        [Fact]
        public void LegalDocumentViewModel_LoadsTheChosenDocument()
        {
            var vm = new LegalDocumentViewModel(Fakes.Dialogs().Object);
            Assert.Equal(AppResources.Privacy_Title, vm.Title);

            vm.Load(LegalDocumentKind.Kvkk);
            Assert.Equal(AppResources.Kvkk_Title, vm.Title);
            Assert.True(vm.ShowContact);
            Assert.NotEmpty(vm.Sections);

            vm.Load(LegalDocumentKind.Licenses);
            Assert.False(vm.ShowContact);
        }

        [Fact]
        public void TheFaq_HasAnsweredQuestionsInEveryCategory()
        {
            var help = DocumentText.ParseHelp(DocumentText.Fill(AppResources.Help_Faq));

            Assert.True(help.Count >= 5);
            Assert.All(help, c => Assert.NotEmpty(c.Questions));
            Assert.All(help.SelectMany(c => c.Questions), q => Assert.False(string.IsNullOrWhiteSpace(q.Answer)));
        }

        [Fact]
        public void HelpCenter_SearchIgnoresCaseAndTurkishLetters()
        {
            var vm = new HelpCenterViewModel(Fakes.Dialogs().Object);
            var all = vm.Categories.Sum(c => c.Items.Count);
            Assert.Equal(vm.QuestionCount, all);

            vm.SearchText = "COP KUTUSU";
            Assert.True(vm.HasResults);
            Assert.InRange(vm.Categories.Sum(c => c.Items.Count), 1, all - 1);
            Assert.All(vm.Categories.SelectMany(c => c.Items), i =>
                Assert.True(TripListFilter.Matches(i.Question + " " + i.Answer, "çöp kutusu", CultureInfo.CurrentCulture)
                            || vm.Categories.Any(c => TripListFilter.Matches(c.Title, "çöp kutusu", CultureInfo.CurrentCulture))));

            vm.SearchText = "qqqzzz";
            Assert.True(vm.NoResults);
            Assert.Empty(vm.Categories);

            vm.ClearSearchCommand.Execute(null);
            Assert.Equal(all, vm.Categories.Sum(c => c.Items.Count));
            Assert.All(vm.Categories.SelectMany(c => c.Items), i => Assert.False(i.IsExpanded));
        }

        [Fact]
        public void HelpCenter_TappingAQuestionOpensAndClosesIt()
        {
            var vm = new HelpCenterViewModel(Fakes.Dialogs().Object);
            var item = vm.Categories[0].Items[0];

            vm.ToggleCommand.Execute(item);
            Assert.True(item.IsExpanded);
            vm.ToggleCommand.Execute(item);
            Assert.False(item.IsExpanded);
        }
    }
}
