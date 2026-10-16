using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class TodoListViewModel : ViewModelBase, ISelectableListViewModel
    {
        private readonly ITodoService _todoService;
        private readonly ITripService _tripService;
        private readonly IWeatherService _weatherService;
        private readonly IPackingService _packingService;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private string newTaskTitle = string.Empty;

        public ObservableCollection<TodoItem> Tasks { get; } = new RangeObservableCollection<TodoItem>();

        /// <summary>"Seçerek sil" mode of the list.</summary>
        public ListSelection<TodoItem> Selection { get; }

        IListSelection ISelectableListViewModel.Selection => Selection;

        /// <summary>The app bar's trash button: only with something to delete and the right to delete it.</summary>
        public bool ShowDeleteMenu => CanEdit && HasTasks;

        [ObservableProperty]
        private bool hasTasks;

        [ObservableProperty]
        private bool hasNoTasks; // false until the first load has run, so the empty state never flashes

        // ---- Smart packing list (TP-405) ----

        /// <summary>Suggestions shown in the "Akıllı bavul önerileri" panel.</summary>
        public ObservableCollection<PackingSuggestion> Suggestions { get; } = new RangeObservableCollection<PackingSuggestion>();

        [ObservableProperty]
        private bool isSuggestionPanelVisible;

        [ObservableProperty]
        private bool isLoadingSuggestions;

        [ObservableProperty]
        private bool hasSuggestions;

        /// <summary>Panel open but nothing new to suggest.</summary>
        [ObservableProperty]
        private bool hasNoSuggestions;

        /// <summary>Explains what the suggestions are based on (with or without a forecast).</summary>
        [ObservableProperty]
        private string suggestionHint = string.Empty;

        public TodoListViewModel(
            ITodoService todoService,
            ITripService tripService,
            IWeatherService weatherService,
            IPackingService packingService,
            IDialogService dialogs)
            : base(dialogs)
        {
            _todoService = todoService;
            _tripService = tripService;
            _weatherService = weatherService;
            _packingService = packingService;
            Selection = new ListSelection<TodoItem>(() => Tasks);
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName is nameof(CanEdit) or nameof(HasTasks))
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowDeleteMenu)));
        }

        // NOTE (TP-104): loading is triggered only from TodoListPage.OnAppearing.

        [RelayCommand]
        private Task LoadTasksAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            await RefreshAccessAsync(TripId); // G6: viewers don't get add/edit buttons

            var tasks = await _todoService.GetByTripIdAsync(TripId);
            Tasks.ReplaceAll(tasks);

            UpdateEmptyState();
            Selection.Sync();
        }, AppResources.Todo_LoadError);

        private void UpdateEmptyState()
        {
            HasTasks = Tasks.Count > 0;
            HasNoTasks = !HasTasks;
        }

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");

        [RelayCommand]
        private Task AddTaskAsync() => RunSafeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(NewTaskTitle)) return;

            var task = new TodoItem { TripId = TripId, Title = NewTaskTitle.Trim() };
            await _todoService.SaveAsync(task);
            Tasks.Add(task);
            NewTaskTitle = string.Empty;

            UpdateEmptyState();
        }, AppResources.Todo_AddError);

        [RelayCommand]
        private Task EditTaskAsync(TodoItem task) => RunSafeAsync(async () =>
        {
            if (task is null || Shell.Current is null) return;

            var newTitle = await Shell.Current.DisplayPromptAsync(AppResources.Todo_EditTitle, AppResources.Todo_EditMessage,
                accept: AppResources.Common_SaveShort, cancel: AppResources.Common_Cancel, initialValue: task.Title);
            if (string.IsNullOrWhiteSpace(newTitle)) return;

            var previousTitle = task.Title;
            task.Title = newTitle.Trim();
            try
            {
                await _todoService.SaveAsync(task);
            }
            catch
            {
                // e.g. a viewer of a shared trip: keep showing what is actually stored
                task.Title = previousTitle;
                RefreshTaskRow(task);
                throw;
            }

            RefreshTaskRow(task);
        }, AppResources.Todo_UpdateError);

        /// <summary>
        /// Called by TodoListPage only when the user actually ticks/unticks a box (TP-105) - the page
        /// ignores the CheckedChanged events the binding raises while rows are being created, so loading
        /// the list no longer writes every task back to the database.
        /// </summary>
        [RelayCommand]
        private Task SetCompletedAsync(TodoCompletionChange change) => RunSafeAsync(async () =>
        {
            if (change?.Task is null || change.Task.IsCompleted == change.IsCompleted) return;

            change.Task.IsCompleted = change.IsCompleted;
            try
            {
                await _todoService.SaveAsync(change.Task);
            }
            catch
            {
                change.Task.IsCompleted = !change.IsCompleted;
                RefreshTaskRow(change.Task);
                throw;
            }

            RefreshTaskRow(change.Task);
        }, AppResources.Todo_UpdateError);

        private void RefreshTaskRow(TodoItem task)
        {
            var index = Tasks.IndexOf(task);
            if (index >= 0)
                Tasks[index] = task;
        }

        // ================= TP-405: smart packing list =================

        [RelayCommand]
        private Task ShowSuggestionsAsync() => RunOnceAsync(async () =>
        {
            if (TripId == 0) return;

            IsSuggestionPanelVisible = true;
            IsLoadingSuggestions = true;
            Suggestions.Clear();
            HasSuggestions = HasNoSuggestions = false;

            try
            {
                var trip = await _tripService.GetByIdAsync(TripId);
                if (trip is null) return;

                // Works offline too - without a forecast the suggestions are based on length and destination.
                var forecast = await _weatherService.GetTripForecastAsync(trip);
                SuggestionHint = forecast is { Count: > 0 } ? AppResources.Packing_SuggestionsHint : AppResources.Packing_NoForecastHint;

                // The plan (activities, places, trip details) adds items for what we'll do there: beach, hiking, meetings…
                IReadOnlyList<PackingPlanEntry>? plan = null;
                try
                {
                    plan = await _packingService.GetPlanAsync(trip);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Packing] Reading the plan failed: {ex.Message}");
                }

                // Matching every rule against a long plan is real CPU work: off the UI thread, then one list update.
                var titles = Tasks.Select(t => t.Title).ToList();
                var suggestions = await Task.Run(() => _packingService.GetSuggestions(trip, forecast, titles, plan).ToList());
                Suggestions.ReplaceAll(suggestions);
            }
            finally
            {
                IsLoadingSuggestions = false;
                HasSuggestions = Suggestions.Count > 0;
                HasNoSuggestions = !HasSuggestions;
            }
        }, AppResources.Packing_Error);

        [RelayCommand]
        private void HideSuggestions()
        {
            IsSuggestionPanelVisible = false;
            Suggestions.Clear();
        }

        [RelayCommand]
        private void ToggleSuggestion(PackingSuggestion? suggestion)
        {
            if (suggestion is not null)
                suggestion.IsSelected = !suggestion.IsSelected;
        }

        [RelayCommand]
        private Task AddSelectedSuggestionsAsync() => RunSafeAsync(async () =>
        {
            var selected = Suggestions.Where(s => s.IsSelected).Select(s => s.Title).ToList();
            await AddTitlesAsync(selected);
            HideSuggestions();
        }, AppResources.Todo_AddError);

        /// <summary>Template actions behind one button: save the current list, apply one, or delete one.</summary>
        [RelayCommand]
        private Task ManageTemplatesAsync() => RunSafeAsync(async () =>
        {
            var action = await Dialogs.ChooseAsync(AppResources.Packing_TemplateMenuTitle, new[]
            {
                AppResources.Packing_ApplyTemplate,
                AppResources.Packing_SaveAsTemplate,
                AppResources.Packing_DeleteTemplate,
            });

            if (action == AppResources.Packing_SaveAsTemplate)
                await SaveAsTemplateAsync();
            else if (action == AppResources.Packing_ApplyTemplate)
                await ApplyTemplateAsync();
            else if (action == AppResources.Packing_DeleteTemplate)
                await DeleteTemplateAsync();
        }, AppResources.Packing_Error);

        private async Task SaveAsTemplateAsync()
        {
            if (Tasks.Count == 0)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Packing_EmptyListForTemplate);
                return;
            }

            var name = await Dialogs.PromptAsync(AppResources.Packing_TemplateNameTitle, AppResources.Packing_TemplateNameMessage);
            if (string.IsNullOrWhiteSpace(name))
                return;

            var template = await _packingService.SaveTemplateAsync(name, Tasks.Select(t => t.Title));
            await Dialogs.ShowMessageAsync(AppResources.Common_Info,
                string.Format(AppResources.Packing_TemplateSavedFormat, template.Name, template.ItemList.Count));
        }

        private async Task ApplyTemplateAsync()
        {
            var template = await ChooseTemplateAsync();
            if (template is null)
                return;

            var added = await AddTitlesAsync(template.ItemList);
            await Dialogs.ShowMessageAsync(AppResources.Common_Info,
                string.Format(AppResources.Packing_TemplateAppliedFormat, template.Name, added));
        }

        private async Task DeleteTemplateAsync()
        {
            var template = await ChooseTemplateAsync();
            if (template is null)
                return;

            if (!await Dialogs.ConfirmAsync(AppResources.Packing_DeleteTemplate, template.Name))
                return;

            await _packingService.DeleteTemplateAsync(template);
            await Dialogs.ShowMessageAsync(AppResources.Common_Info, string.Format(AppResources.Packing_TemplateDeletedFormat, template.Name));
        }

        private async Task<PackingTemplate?> ChooseTemplateAsync()
        {
            var templates = await _packingService.GetTemplatesAsync();
            if (templates.Count == 0)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Packing_NoTemplates);
                return null;
            }

            var choice = await Dialogs.ChooseAsync(AppResources.Packing_ChooseTemplate, templates.Select(t => t.Display).ToList());
            return templates.FirstOrDefault(t => t.Display == choice);
        }

        /// <summary>Adds each title not already on the list (case-insensitive). Returns how many were added.</summary>
        private async Task<int> AddTitlesAsync(IEnumerable<string> titles)
        {
            var existing = new HashSet<string>(Tasks.Select(t => t.Title.Trim()), StringComparer.CurrentCultureIgnoreCase);
            var added = 0;

            foreach (var title in titles.Select(t => t.Trim()).Where(t => t.Length > 0))
            {
                if (!existing.Add(title))
                    continue;

                var task = new TodoItem { TripId = TripId, Title = title };
                await _todoService.SaveAsync(task);
                Tasks.Add(task);
                added++;
            }

            UpdateEmptyState();
            return added;
        }

        // ================= Selecting rows and deleting them together =================

        /// <summary>A tap on a row: ticks it in selection mode (outside it the row's own buttons do the work).</summary>
        [RelayCommand]
        private void TaskTapped(TodoItem? task)
        {
            if (Selection.IsActive)
                Selection.Toggle(task);
        }

        [RelayCommand]
        private Task DeleteSelectedAsync() => RunSafeAsync(async () =>
        {
            await DeleteManyAsync(Selection.SelectedItems, Selection.DeleteTitle);
        }, AppResources.Todo_DeleteError);

        private async Task DeleteManyAsync(IReadOnlyList<TodoItem> tasks, string title)
        {
            if (tasks.Count == 0)
                return;

            var confirmed = tasks.Count == 1
                ? await Dialogs.ConfirmAsync(AppResources.Todo_DeleteTitle, string.Format(AppResources.Todo_DeleteMessage, tasks[0].Title))
                : await ConfirmBulkDeleteAsync(title, string.Format(AppResources.TripDelete_TodosFormat, tasks.Count));
            if (!confirmed)
                return;

            try
            {
                await DeleteEachAsync(tasks, t => _todoService.DeleteAsync(t), t => Tasks.Remove(t));
            }
            finally
            {
                UpdateEmptyState();
                Selection.Cancel();
            }
        }
    }

    /// <summary>Payload for SetCompletedCommand: which task, and the checkbox's new value.</summary>
    public record TodoCompletionChange(TodoItem Task, bool IsCompleted);
}
