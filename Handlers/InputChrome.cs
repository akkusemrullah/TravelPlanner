using System.Runtime.CompilerServices;
using Microsoft.Maui.Handlers;

namespace TravelPlanner.Handlers
{
    /// <summary>
    /// One look for every text field in the app. The visible chrome is the "Input" Border around each control
    /// (Resources/Styles/Styles.xaml); this removes what the platforms draw on top of it:
    /// - Android: the Material underline of EditText-based controls (Entry, Editor, Picker, DatePicker,
    ///   TimePicker) and the search plate line of SearchBar;
    /// - iOS / Mac Catalyst: the rounded-rect border of UITextField-based controls.
    /// It also highlights the surrounding Input Border (accent stroke, surface fill) while the field has focus.
    /// Registered once in MauiProgram.
    /// </summary>
    public static class InputChrome
    {
        private const string Key = "TP.InputChrome";
        private static readonly ConditionalWeakTable<VisualElement, object> Hooked = new();

        public static void Configure()
        {
            // Mapped on the custom key (first creation) and after "Background" (MAUI re-applies the platform
            // background - and with it the underline - whenever BackgroundColor changes, e.g. on a theme switch).
            foreach (var key in new[] { Key, nameof(IView.Background) })
            {
                EntryHandler.Mapper.AppendToMapping(key, (handler, view) =>
                {
#if ANDROID
                    RemoveUnderline(handler.PlatformView);
#elif IOS || MACCATALYST
                    handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
                    HookFocus(view);
                });

                EditorHandler.Mapper.AppendToMapping(key, (handler, view) =>
                {
#if ANDROID
                    RemoveUnderline(handler.PlatformView);
#endif
                    HookFocus(view);
                });

                PickerHandler.Mapper.AppendToMapping(key, (handler, view) =>
                {
#if ANDROID
                    RemoveUnderline(handler.PlatformView);
#elif IOS || MACCATALYST
                    handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
                });

                DatePickerHandler.Mapper.AppendToMapping(key, (handler, view) =>
                {
#if ANDROID
                    RemoveUnderline(handler.PlatformView);
#elif IOS || MACCATALYST
                    // Text-field based on iOS; Mac Catalyst uses the native UIDatePicker (no border to remove).
                    if (handler.PlatformView is UIKit.UITextField field)
                        field.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
                });

                TimePickerHandler.Mapper.AppendToMapping(key, (handler, view) =>
                {
#if ANDROID
                    RemoveUnderline(handler.PlatformView);
#elif IOS || MACCATALYST
                    // Text-field based on iOS; Mac Catalyst uses the native UIDatePicker (no border to remove).
                    if (handler.PlatformView is UIKit.UITextField field)
                        field.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
                });

                SearchBarHandler.Mapper.AppendToMapping(key, (handler, view) =>
                {
#if ANDROID
                    try
                    {
                        // SearchView draws its underline on the inner "search_plate" view.
                        var searchView = handler.PlatformView;
                        var plateId = searchView.Context?.Resources?.GetIdentifier("android:id/search_plate", null, null) ?? 0;
                        if (plateId != 0)
                            searchView.FindViewById(plateId)?.SetBackgroundColor(Android.Graphics.Color.Transparent);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[InputChrome] search plate: {ex.Message}");
                    }
#endif
                    HookFocus(view);
                });
            }
        }

#if ANDROID
        /// <summary>Tints the EditText's Material underline drawable transparent (keeps its padding and height).</summary>
        private static void RemoveUnderline(Android.Views.View? view)
        {
            if (view is null)
                return;

            try
            {
                view.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InputChrome] underline: {ex.Message}");
            }
        }
#endif

        // ---- Focus highlight on the surrounding Input border ----

        private static void HookFocus(IView view)
        {
            if (view is not VisualElement element || Hooked.TryGetValue(element, out _))
                return;

            Hooked.Add(element, new object());
            element.Focused += (_, _) => SetHighlighted(element, true);
            element.Unfocused += (_, _) => SetHighlighted(element, false);
        }

        private static void SetHighlighted(VisualElement element, bool focused)
        {
            var border = FindInputBorder(element);
            if (border is null)
                return;

            if (focused)
            {
                border.SetDynamicResource(Border.StrokeProperty, "tp-accent");
                border.SetDynamicResource(VisualElement.BackgroundColorProperty, "tp-surface");
                border.StrokeThickness = 1.5;
            }
            else
            {
                // Back to whatever the Input style says (keeps following the light/dark tokens).
                border.ClearValue(Border.StrokeProperty);
                border.ClearValue(VisualElement.BackgroundColorProperty);
                border.ClearValue(Border.StrokeThicknessProperty);
            }
        }

        /// <summary>The nearest ancestor Border (at most three levels up) that uses the Input / Input.Multiline style.</summary>
        private static Border? FindInputBorder(Element element)
        {
            var parent = element.Parent;
            for (var depth = 0; parent is not null && depth < 3; depth++, parent = parent.Parent)
            {
                if (parent is Border border)
                    return IsInputStyle(border.Style) ? border : null;
            }

            return null;
        }

        private static bool IsInputStyle(Style? style)
        {
            var resources = Application.Current?.Resources;
            if (style is null || resources is null)
                return false;

            for (var current = style; current is not null; current = current.BasedOn)
            {
                if ((resources.TryGetValue("Input", out var input) && ReferenceEquals(current, input)) ||
                    (resources.TryGetValue("Input.Multiline", out var multiline) && ReferenceEquals(current, multiline)))
                    return true;
            }

            return false;
        }
    }
}
