using Microsoft.Maui.Controls.Shapes;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Views
{
    /// <summary>
    /// The app's own dialogs in place of the platform alert / action sheet / prompt (DialogService): a rounded card
    /// in the middle of a dimmed page for questions, messages and text input, and a sheet from the bottom for a
    /// choice between options. Colours, type and buttons come from the app's styles, so they follow the theme.
    /// A transparent modal page; the back button and a tap on the dimmed area cancel.
    /// </summary>
    public sealed class AppDialogPage : ContentPage
    {
        public enum Tone
        {
            Info,
            Danger,
            Error,
        }

        private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly BoxView _scrim;
        private readonly Border _panel;
        private readonly bool _isSheet;
        private Entry? _entry;
        private bool _closing;

        private AppDialogPage(View content, bool isSheet)
        {
            _isSheet = isSheet;
            BackgroundColor = Colors.Transparent;
            Shell.SetPresentationMode(this, PresentationMode.ModalNotAnimated);
            NavigationPage.SetHasNavigationBar(this, false);

            _scrim = new BoxView { Opacity = 0 };
            _scrim.SetDynamicResource(BoxView.ColorProperty, "tp-overlay");
            _scrim.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Close(null)) });

            _panel = new Border
            {
                Content = content,
                StrokeThickness = 0,
                Opacity = 0,
                Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.18f, Radius = 24, Offset = new Point(0, 8) },
            };
            _panel.SetDynamicResource(BackgroundColorProperty, "tp-surface");
            if (isSheet)
            {
                _panel.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(24, 24, 0, 0) };
                _panel.Padding = new Thickness(0, 10, 0, 20);
                _panel.VerticalOptions = LayoutOptions.End;
                _panel.MaximumWidthRequest = 560;
            }
            else
            {
                _panel.StrokeShape = new RoundRectangle { CornerRadius = 20 };
                _panel.Padding = new Thickness(24, 24, 24, 20);
                _panel.Margin = new Thickness(24);
                _panel.VerticalOptions = LayoutOptions.Center;
                _panel.MaximumWidthRequest = 420;
            }

            Content = new Grid { Children = { _scrim, _panel } };
        }

        private Task<string?> Result => _result.Task;

        // ------------------------------------------------------------------------------------------- builders

        /// <summary>A question or a message: icon, title, text and up to two buttons (the first is the main one).</summary>
        public static Task<string?> ShowCardAsync(INavigation navigation, string title, string message, string accept,
                                                  string? cancel, Tone tone, bool withInput = false, string? initialValue = null)
        {
            var stack = new VerticalStackLayout { Spacing = 0 };
            stack.Add(IconTile(tone));

            var heading = new Label { Text = title, Style = Res<Style>("Text.H2"), LineBreakMode = LineBreakMode.WordWrap, Margin = new Thickness(0, 16, 0, 0) };
            SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
            stack.Add(heading);
            if (!string.IsNullOrWhiteSpace(message))
                stack.Add(new Label { Text = message, Style = Res<Style>("Text.Body2"), LineBreakMode = LineBreakMode.WordWrap, LineHeight = 1.3, Margin = new Thickness(0, 8, 0, 0) });

            Entry? entry = null;
            if (withInput)
            {
                entry = new Entry { Text = initialValue ?? string.Empty, ReturnType = ReturnType.Done };
                stack.Add(new Border { Style = Res<Style>("Input"), Content = entry, Margin = new Thickness(0, 16, 0, 0) });
            }

            var page = new AppDialogPage(stack, isSheet: false) { _entry = entry };

            var buttons = new VerticalStackLayout { Spacing = 10, Margin = new Thickness(0, 24, 0, 0) };
            var main = new Button { Text = accept, Style = Res<Style>("Btn.Primary") };
            if (tone == Tone.Danger)
            {
                main.SetDynamicResource(BackgroundColorProperty, "tp-danger");
                main.TextColor = Colors.White;
            }
            main.Clicked += (_, _) => page.Close(withInput ? page._entry?.Text ?? string.Empty : accept);
            if (entry is not null)
                entry.Completed += (_, _) => page.Close(entry.Text ?? string.Empty);
            buttons.Add(main);
            if (cancel is not null)
            {
                var back = new Button { Text = cancel, Style = Res<Style>("Btn.Secondary") };
                back.Clicked += (_, _) => page.Close(null);
                buttons.Add(back);
            }
            stack.Add(buttons);

            return page.PresentAsync(navigation);
        }

        /// <summary>A choice from the bottom: title, one row per option and Vazgeç.</summary>
        public static Task<string?> ShowSheetAsync(INavigation navigation, string title, IReadOnlyList<string> options, string cancel)
        {
            var stack = new VerticalStackLayout { Spacing = 0 };
            var handle = new BoxView { WidthRequest = 40, HeightRequest = 4, CornerRadius = 2, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 0, 0, 14) };
            handle.SetDynamicResource(BoxView.ColorProperty, "tp-border-strong");
            stack.Add(handle);

            if (!string.IsNullOrWhiteSpace(title))
            {
                var heading = new Label { Text = title, Style = Res<Style>("Text.H3"), LineBreakMode = LineBreakMode.WordWrap, Margin = new Thickness(20, 0, 20, 10) };
                SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
                stack.Add(heading);
            }

            var page = new AppDialogPage(stack, isSheet: true); // no ScrollView: it would stretch the sheet to the whole screen

            var list = new VerticalStackLayout { Spacing = 0, Margin = new Thickness(12, 0) };
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                if (i > 0)
                    list.Add(new BoxView { Style = Res<Style>("Divider"), Margin = new Thickness(12, 0) });

                var row = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                    Padding = new Thickness(12, 15),
                    BackgroundColor = Colors.Transparent,
                };
                row.Add(new Label { Text = option, Style = Res<Style>("Text.Body"), LineBreakMode = LineBreakMode.WordWrap, VerticalOptions = LayoutOptions.Center });
                var chevron = new Label { Text = Res<string>("Ic.ChevronRight"), Style = Res<Style>("Icon.Sm"), VerticalOptions = LayoutOptions.Center };
                chevron.SetDynamicResource(Label.TextColorProperty, "tp-text-3");
                row.Add(chevron, 1);
                row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => page.Close(option)) });
                SemanticProperties.SetDescription(row, option);
                AutomationProperties.SetIsInAccessibleTree(row, true);
                list.Add(row);
            }
            stack.Add(list);

            var back = new Button { Text = cancel, Style = Res<Style>("Btn.Secondary"), Margin = new Thickness(20, 14, 20, 0) };
            back.Clicked += (_, _) => page.Close(null);
            stack.Add(back);

            return page.PresentAsync(navigation);
        }

        // ------------------------------------------------------------------------------------------- show / close

        private async Task<string?> PresentAsync(INavigation navigation)
        {
            await navigation.PushModalAsync(this, animated: false);
            return await Result;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (_isSheet)
            {
                _panel.TranslationY = 400;
                _panel.Opacity = 1;
                await Task.WhenAll(
                    _scrim.FadeToAsync(0.45, 180, Easing.CubicOut),
                    _panel.TranslateToAsync(0, 0, 240, Easing.CubicOut));
            }
            else
            {
                _panel.Scale = 0.94;
                await Task.WhenAll(
                    _scrim.FadeToAsync(0.45, 160, Easing.CubicOut),
                    _panel.FadeToAsync(1, 160, Easing.CubicOut),
                    _panel.ScaleToAsync(1, 200, Easing.CubicOut));
            }
            _entry?.Focus();
        }

        protected override bool OnBackButtonPressed()
        {
            Close(null);
            return true;
        }

        private async void Close(string? result)
        {
            if (_closing)
                return;
            _closing = true;
            _entry?.Unfocus();

            try
            {
                if (_isSheet)
                    await Task.WhenAll(_scrim.FadeToAsync(0, 140), _panel.TranslateToAsync(0, _panel.Height + 40, 180, Easing.CubicIn));
                else
                    await Task.WhenAll(_scrim.FadeToAsync(0, 120), _panel.FadeToAsync(0, 120));
                await Navigation.PopModalAsync(animated: false);
            }
            catch (Exception ex)
            {
                // the page is already gone (navigation changed underneath) - the answer still goes back
                System.Diagnostics.Debug.WriteLine($"[Dialog] Close failed: {ex.Message}");
            }
            finally
            {
                _result.TrySetResult(result);
            }
        }

        // ------------------------------------------------------------------------------------------- helpers

        private static View IconTile(Tone tone)
        {
            var (glyph, fill, color) = tone switch
            {
                Tone.Danger => ("Ic.Alert", "tp-danger-soft", "tp-danger"),
                Tone.Error => ("Ic.Alert", "tp-danger-soft", "tp-danger"),
                _ => ("Ic.Info", "tp-accent-soft", "tp-accent-text"),
            };
            var icon = new Label { Text = Res<string>(glyph), Style = Res<Style>("Icon"), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
            icon.SetDynamicResource(Label.TextColorProperty, color);
            var tile = new Border
            {
                WidthRequest = 48,
                HeightRequest = 48,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                HorizontalOptions = LayoutOptions.Start,
                Content = icon,
            };
            tile.SetDynamicResource(BackgroundColorProperty, fill);
            return tile;
        }

        private static T Res<T>(string key) =>
            Application.Current is { } app && app.Resources.TryGetValue(key, out var value) && value is T typed
                ? typed
                : throw new InvalidOperationException($"Resource '{key}' is missing.");

        /// <summary>Button text of a confirmation that deletes (the default "Sil").</summary>
        internal static bool IsDestructive(string? accept) =>
            accept is null || string.Equals(accept, AppResources.Common_DeleteConfirm, StringComparison.CurrentCulture);
    }
}
