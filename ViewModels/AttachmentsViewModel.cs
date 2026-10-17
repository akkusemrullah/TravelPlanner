using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Media;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// Tickets, bookings, visa PDFs and photos attached to a trip (TP-501). Everything picked is copied into the
    /// app, so it opens without internet and survives the original being deleted.
    /// </summary>
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class AttachmentsViewModel : ViewModelBase, ISelectableListViewModel
    {
        private readonly IAttachmentService _attachments;
        private readonly IMediaPicker _mediaPicker;
        private readonly IFilePicker _filePicker;
        private readonly ILauncher _launcher;

        /// <summary>PDFs and images, per platform (Android MIME types, Apple UTIs, Windows extensions).</summary>
        internal static readonly FilePickerFileType DocumentTypes = new(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            // TP-3: exactly what the cloud accepts (AttachmentService.CloudContentType)
            [DevicePlatform.Android] = new[] { "application/pdf", "image/jpeg", "image/png", "image/heic", "image/heif", "image/webp" },
            [DevicePlatform.iOS] = new[] { "com.adobe.pdf", "public.image" },
            [DevicePlatform.MacCatalyst] = new[] { "com.adobe.pdf", "public.image" },
            [DevicePlatform.WinUI] = new[] { ".pdf", ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp" },
        });

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private bool hasAttachments;

        [ObservableProperty]
        private bool hasNoAttachments;

        public ObservableCollection<TripAttachment> Attachments { get; } = new RangeObservableCollection<TripAttachment>();

        /// <summary>"Seçerek sil" mode of the list.</summary>
        public ListSelection<TripAttachment> Selection { get; }

        IListSelection ISelectableListViewModel.Selection => Selection;

        /// <summary>The app bar's trash button: only with something to delete and the right to delete it.</summary>
        public bool ShowDeleteMenu => CanEdit && HasAttachments;

        /// <summary>Add tiles and the rename buttons: editors only, and out of the way while rows are being ticked.</summary>
        public bool ShowEditActions => CanEdit && !Selection.IsActive;

        public bool CanTakePhoto => _mediaPicker.IsCaptureSupported;

        public AttachmentsViewModel(
            IAttachmentService attachments,
            IMediaPicker mediaPicker,
            IFilePicker filePicker,
            ILauncher launcher,
            IDialogService dialogs)
            : base(dialogs)
        {
            _attachments = attachments;
            _mediaPicker = mediaPicker;
            _filePicker = filePicker;
            _launcher = launcher;
            Selection = new ListSelection<TripAttachment>(() => Attachments);
            Selection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Selection.IsActive))
                    OnPropertyChanged(nameof(ShowEditActions));
            };
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName is nameof(CanEdit) or nameof(HasAttachments))
            {
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowDeleteMenu)));
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowEditActions)));
            }
        }

        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            await RefreshAccessAsync(TripId); // G6: viewers don't get add/edit buttons

            var items = await _attachments.GetByTripIdAsync(TripId);
            Attachments.ReplaceAll(items);

            UpdateEmptyState();
            Selection.Sync();
            DownloadMissingPhotosAsync(items).FireAndForget();
        }, AppResources.Attachments_LoadError);

        /// <summary>
        /// G5: photos added on another device arrive as rows only - fetch their files in the background so the tiles
        /// show them (documents are fetched when opened).
        /// </summary>
        private async Task DownloadMissingPhotosAsync(IReadOnlyList<TripAttachment> items)
        {
            foreach (var item in items.Where(i => i.IsPhoto && !string.IsNullOrEmpty(i.RemotePath) && !File.Exists(i.FullPath)))
            {
                if (!await _attachments.EnsureLocalFileAsync(item))
                    continue;

                // Re-insert so the tile's image binding picks up the file.
                var index = Attachments.IndexOf(item);
                if (index >= 0)
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        var at = Attachments.IndexOf(item);
                        if (at >= 0)
                        {
                            Attachments.RemoveAt(at);
                            Attachments.Insert(at, item);
                        }
                    });
            }
        }

        private void UpdateEmptyState()
        {
            HasAttachments = Attachments.Count > 0;
            HasNoAttachments = !HasAttachments;
        }

        [RelayCommand]
        private Task TakePhotoAsync() => PickAndStoreAsync(() => _mediaPicker.CapturePhotoAsync());

        [RelayCommand]
        // MAUI 10: PickPhotoAsync is obsolete - PickPhotosAsync with a limit of one photo.
        private Task PickPhotoAsync() => PickAndStoreAsync(async () =>
            (await _mediaPicker.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1 }))?.FirstOrDefault());

        [RelayCommand]
        private Task PickDocumentAsync() => PickAndStoreAsync(() => _filePicker.PickAsync(new PickOptions
        {
            PickerTitle = AppResources.Attachments_PickDocument,
            FileTypes = DocumentTypes
        }));

        /// <summary>Shared flow for all three sources: pick → copy into the app → add to the list.</summary>
        private Task PickAndStoreAsync(Func<Task<FileResult?>> pick) => RunSafeAsync(async () =>
        {
            if (TripId == 0) return;

            FileResult? file;
            try
            {
                file = await pick();
            }
            catch (FeatureNotSupportedException)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Attachments_NotSupported);
                return;
            }
            catch (PermissionException)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Attachments_PermissionDenied);
                return;
            }

            if (file is null)
                return; // user cancelled

            try
            {
                await using var stream = await file.OpenReadAsync();
                var attachment = await _attachments.AddAsync(TripId, stream, file.FileName, file.ContentType);
                Attachments.Insert(0, attachment);
                UpdateEmptyState();
            }
            catch (AttachmentTooLargeException ex)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info,
                    string.Format(AppResources.Attachments_TooLargeFormat, TripAttachment.FormatSize(ex.MaxBytes)));
            }
            catch (UnsupportedAttachmentTypeException)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Attachments_UnsupportedType);
            }
        }, AppResources.Attachments_AddError);

        [RelayCommand]
        private Task OpenAsync(TripAttachment? attachment) => RunSafeAsync(async () =>
        {
            if (attachment is null) return;

            // Added on another device? Then the file is downloaded first (G5).
            if (!await _attachments.EnsureLocalFileAsync(attachment))
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Attachments_FileMissing);
                return;
            }

            // TP-5: the type comes from the file's extension and the photo/PDF whitelist - never from the server's
            // content_type (an editor could set it to an APK and start the package installer on everyone's phone)
            var contentType = AttachmentService.OpenContentType(attachment);
            if (contentType is null && !string.IsNullOrEmpty(attachment.RemotePath))
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Attachments_NotSafeToOpen);
                return;
            }

            // Hands the file to the device's viewer (PDF reader, gallery…) - works offline. (null: a file picked on
            // this device before TP-3, which never left it - the device guesses its type as before.)
            await _launcher.OpenAsync(new OpenFileRequest(attachment.DisplayName, new ReadOnlyFile(attachment.FullPath, contentType)));
        }, AppResources.Attachments_OpenError);

        [RelayCommand]
        private Task RenameAsync(TripAttachment? attachment) => RunSafeAsync(async () =>
        {
            if (attachment is null) return;

            var name = await Dialogs.PromptAsync(AppResources.Attachments_RenameTitle, AppResources.Attachments_RenameMessage, attachment.DisplayName);
            if (string.IsNullOrWhiteSpace(name)) return;

            await _attachments.RenameAsync(attachment, name);
            var index = Attachments.IndexOf(attachment);
            if (index >= 0)
                Attachments[index] = attachment;
        }, AppResources.Attachments_AddError);

        // ================= Selecting rows and deleting them together =================

        /// <summary>A tap on a row: opens the file, or ticks it in selection mode.</summary>
        [RelayCommand]
        private async Task AttachmentTappedAsync(TripAttachment? attachment)
        {
            if (attachment is null) return;

            if (Selection.IsActive)
                Selection.Toggle(attachment);
            else
                await OpenAsync(attachment);
        }

        [RelayCommand]
        private Task DeleteSelectedAsync() =>
            RunSafeAsync(() => DeleteManyAsync(Selection.SelectedItems, Selection.DeleteTitle), AppResources.Attachments_DeleteError);

        private async Task DeleteManyAsync(IReadOnlyList<TripAttachment> attachments, string title)
        {
            if (attachments.Count == 0)
                return;

            var confirmed = attachments.Count == 1
                ? await Dialogs.ConfirmAsync(AppResources.Attachments_DeleteTitle, string.Format(AppResources.Attachments_DeleteMessage, attachments[0].DisplayName))
                : await ConfirmBulkDeleteAsync(title, string.Format(AppResources.Selection_AttachmentsFormat, attachments.Count));
            if (!confirmed)
                return;

            try
            {
                await DeleteEachAsync(attachments, a => _attachments.DeleteAsync(a), a => Attachments.Remove(a));
            }
            finally
            {
                UpdateEmptyState();
                Selection.Cancel();
            }
        }

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
    }
}
