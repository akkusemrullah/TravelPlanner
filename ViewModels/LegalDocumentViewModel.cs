using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// One legal document (privacy policy, KVKK notice, terms of use, open-source licences), split into titled
    /// sections. Which one comes from the route's "doc" query (<see cref="LegalDocuments"/>).
    /// </summary>
    public partial class LegalDocumentViewModel : ObservableObject
    {
        private readonly IDialogService _dialogs;

        public LegalDocumentViewModel(IDialogService dialogs)
        {
            _dialogs = dialogs;
            Load(LegalDocumentKind.Privacy);
        }

        public LegalDocumentKind Kind { get; private set; }

        public ObservableCollection<DocumentSection> Sections { get; } = new();

        [ObservableProperty]
        private string title = string.Empty;

        [ObservableProperty]
        private string updatedText = string.Empty;

        /// <summary>"Bize yaz" under the text - for the documents people have questions about (not the licences).</summary>
        [ObservableProperty]
        private bool showContact;

        public void Load(LegalDocumentKind kind)
        {
            Kind = kind;
            Title = LegalDocuments.Title(kind);
            UpdatedText = string.Format(CultureInfo.CurrentCulture, AppResources.Legal_UpdatedFormat,
                DocumentText.LastUpdated.ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
            ShowContact = kind != LegalDocumentKind.Licenses;

            Sections.Clear();
            foreach (var section in LegalDocuments.Sections(kind))
                Sections.Add(section);
        }

        [RelayCommand]
        private Task ContactAsync() =>
            SupportContact.ComposeOrShowAddressAsync(_dialogs,
                string.Format(CultureInfo.CurrentCulture, AppResources.Legal_MailSubjectFormat, Title));
    }
}
