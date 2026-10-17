using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>One help-centre question; tapping it opens/closes the answer.</summary>
    public partial class HelpItemView : ObservableObject
    {
        public HelpItemView(HelpQuestion question)
        {
            Question = question.Question;
            Answer = question.Answer;
        }

        public string Question { get; }

        public string Answer { get; }

        [ObservableProperty]
        private bool isExpanded;
    }

    /// <summary>A category of the help centre with the questions that match the current search.</summary>
    public sealed class HelpCategoryView
    {
        public HelpCategoryView(string title, IReadOnlyList<HelpItemView> items)
        {
            Title = title;
            Items = items;
        }

        public string Title { get; }

        public IReadOnlyList<HelpItemView> Items { get; }
    }

    /// <summary>
    /// Yardım merkezi: the FAQ (AppResources.Help_Faq, see <see cref="DocumentText.ParseHelp"/>) with a search that
    /// ignores case and Turkish letters, the support e-mail and links to the legal documents.
    /// </summary>
    public partial class HelpCenterViewModel : ObservableObject
    {
        private readonly IDialogService _dialogs;
        private readonly IReadOnlyList<(string Title, IReadOnlyList<HelpItemView> Items)> _all;

        public HelpCenterViewModel(IDialogService dialogs)
        {
            _dialogs = dialogs;
            _all = DocumentText.ParseHelp(DocumentText.Fill(AppResources.Help_Faq))
                .Select(c => (c.Title, (IReadOnlyList<HelpItemView>)c.Questions.Select(q => new HelpItemView(q)).ToList()))
                .ToList();
            ApplySearch();
        }

        public ObservableCollection<HelpCategoryView> Categories { get; } = new();

        public string SupportEmail => AppConstants.SupportEmail;

        public string ContactHint => string.Format(CultureInfo.CurrentCulture, AppResources.Help_ContactHintFormat, AppConstants.SupportEmail);

        [ObservableProperty]
        private string searchText = string.Empty;

        [ObservableProperty]
        private bool hasResults = true;

        [ObservableProperty]
        private bool noResults;

        /// <summary>Total number of questions (for the header: "28 soru").</summary>
        public int QuestionCount => _all.Sum(c => c.Items.Count);

        partial void OnSearchTextChanged(string value) => ApplySearch();

        private void ApplySearch()
        {
            var query = SearchText?.Trim() ?? string.Empty;
            var culture = CultureInfo.CurrentCulture;

            Categories.Clear();
            foreach (var (title, items) in _all)
            {
                var matching = query.Length == 0 || TripListFilter.Matches(title, query, culture)
                    ? items
                    : items.Where(i => TripListFilter.Matches(i.Question, query, culture) || TripListFilter.Matches(i.Answer, query, culture)).ToList();
                if (matching.Count > 0)
                    Categories.Add(new HelpCategoryView(title, matching));
            }

            // A search narrowed down to a few questions shows their answers right away; clearing it folds all up.
            var shown = Categories.SelectMany(c => c.Items).ToList();
            if (query.Length == 0)
                shown.ForEach(i => i.IsExpanded = false);
            else if (shown.Count <= 3)
                shown.ForEach(i => i.IsExpanded = true);

            HasResults = Categories.Count > 0;
            NoResults = !HasResults;
        }

        [RelayCommand]
        private void Toggle(HelpItemView? item)
        {
            if (item is not null)
                item.IsExpanded = !item.IsExpanded;
        }

        [RelayCommand]
        private void ClearSearch() => SearchText = string.Empty;

        [RelayCommand]
        private Task ContactAsync() =>
            SupportContact.ComposeOrShowAddressAsync(_dialogs, AppResources.Help_MailSubject);

        [RelayCommand]
        private Task OpenDocumentAsync(string? key) =>
            Shell.Current.GoToAsync($"{Routes.Privacy}?doc={LegalDocuments.Key(LegalDocuments.FromKey(key))}");

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
    }
}
