using Microsoft.Maui.Controls.Shapes;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.Views
{
    /// <summary>
    /// Kapağı kırp: a card over the trip form, like the app's dialogs (<see cref="AppDialogPage"/>). The frame has the
    /// cover's 16:9 shape; the user drags the photo under it and zooms with two fingers. The photo always fills the
    /// frame, so whatever they choose is a whole 16:9 cover. The back button and a tap on the dimmed area cancel.
    /// </summary>
    public sealed class CoverCropDialog : ContentPage
    {
        private readonly TaskCompletionSource<CropRegion?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PickedPhoto _photo;
        private readonly BoxView _scrim;
        private readonly Border _panel;
        private readonly AbsoluteLayout _stage;
        private readonly Image _image;
        private double _frameWidth;
        private double _frameHeight;
        private double _baseScale = 1;
        private double _zoom = 1;
        private double _offsetX;
        private double _offsetY;
        private double _panStartX;
        private double _panStartY;
        private bool _closing;

        private CoverCropDialog(PickedPhoto photo)
        {
            _photo = photo;
            BackgroundColor = Colors.Transparent;
            Shell.SetPresentationMode(this, PresentationMode.ModalNotAnimated);
            NavigationPage.SetHasNavigationBar(this, false);

            _scrim = new BoxView { Opacity = 0 };
            _scrim.SetDynamicResource(BoxView.ColorProperty, "tp-overlay");
            _scrim.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Close(null)) });

            var heading = new Label { Text = AppResources.TripForm_CropTitle, Style = Res<Style>("Text.H2") };
            SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
            var hint = new Label { Text = AppResources.TripForm_CropHint, Style = Res<Style>("Text.Body2"), Margin = new Thickness(0, 6, 0, 14) };

            _image = new Image { Source = ImageSource.FromFile(photo.File), Aspect = Aspect.Fill, InputTransparent = true };
            _stage = new AbsoluteLayout { BackgroundColor = Colors.Black, IsClippedToBounds = true, Children = { _image } };
            _stage.SizeChanged += (_, _) => LayoutPhoto();
            var pinch = new PinchGestureRecognizer();
            pinch.PinchUpdated += OnPinch;
            var pan = new PanGestureRecognizer();
            pan.PanUpdated += OnPan;
            _stage.GestureRecognizers.Add(pinch);
            _stage.GestureRecognizers.Add(pan);

            var frame = new Border
            {
                Content = _stage,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
            };
            // 16:9 at whatever width the card gets
            frame.SizeChanged += (_, _) =>
            {
                var height = Math.Round(frame.Width * TripCoverService.CoverHeight / TripCoverService.CoverWidth);
                if (frame.Width > 0 && Math.Abs(frame.HeightRequest - height) >= 1)
                    frame.HeightRequest = height;
            };

            var cancel = new Button { Text = AppResources.Common_Cancel, Style = Res<Style>("Btn.Secondary") };
            cancel.Clicked += (_, _) => Close(null);
            var use = new Button { Text = AppResources.TripForm_CropUse, Style = Res<Style>("Btn.Primary") };
            use.Clicked += (_, _) => Close(CurrentRegion());
            var buttons = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 10,
                Margin = new Thickness(0, 20, 0, 0),
            };
            buttons.Add(cancel);
            buttons.Add(use, 1);

            _panel = new Border
            {
                Content = new VerticalStackLayout { Spacing = 0, Children = { heading, hint, frame, buttons } },
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 20 },
                Padding = new Thickness(20, 22, 20, 20),
                Margin = new Thickness(16),
                VerticalOptions = LayoutOptions.Center,
                MaximumWidthRequest = 560,
                Opacity = 0,
                Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.18f, Radius = 24, Offset = new Point(0, 8) },
            };
            _panel.SetDynamicResource(BackgroundColorProperty, "tp-surface");

            Content = new Grid { Children = { _scrim, _panel } };
        }

        /// <summary>Shows the photo under the frame; the chosen part, or null when cancelled.</summary>
        public static async Task<CropRegion?> ShowAsync(INavigation navigation, PickedPhoto photo)
        {
            var page = new CoverCropDialog(photo);
            await navigation.PushModalAsync(page, animated: false);
            return await page._result.Task;
        }

        private CropRegion CurrentRegion() =>
            _frameWidth > 0 && _frameHeight > 0
                ? CoverCropMath.Region(_photo.Width, _photo.Height, _frameWidth, _frameHeight, _zoom, _offsetX, _offsetY)
                : CoverCropMath.CenterRegion(_photo.Width, _photo.Height, TripCoverService.CoverWidth, TripCoverService.CoverHeight);

        // ------------------------------------------------------------------------------------------- moving the photo

        private void LayoutPhoto()
        {
            if (_stage.Width <= 0 || _stage.Height <= 0)
                return;
            _frameWidth = _stage.Width;
            _frameHeight = _stage.Height;
            _baseScale = CoverCropMath.CoverScale(_photo.Width, _photo.Height, _frameWidth, _frameHeight);
            var width = _photo.Width * _baseScale;
            var height = _photo.Height * _baseScale;
            // the photo at zoom 1, centred; zoom and moves are transforms around its centre
            AbsoluteLayout.SetLayoutBounds(_image, new Rect((_frameWidth - width) / 2, (_frameHeight - height) / 2, width, height));
            Apply();
        }

        private void Apply()
        {
            var shownWidth = _photo.Width * _baseScale * _zoom;
            var shownHeight = _photo.Height * _baseScale * _zoom;
            _offsetX = CoverCropMath.ClampOffset(_offsetX, shownWidth, _frameWidth);
            _offsetY = CoverCropMath.ClampOffset(_offsetY, shownHeight, _frameHeight);
            _image.Scale = _zoom;
            _image.TranslationX = _offsetX;
            _image.TranslationY = _offsetY;
        }

        private void OnPan(object? sender, PanUpdatedEventArgs e)
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _panStartX = _offsetX;
                    _panStartY = _offsetY;
                    break;
                case GestureStatus.Running:
                    _offsetX = _panStartX + e.TotalX;
                    _offsetY = _panStartY + e.TotalY;
                    Apply();
                    break;
            }
        }

        private void OnPinch(object? sender, PinchGestureUpdatedEventArgs e)
        {
            if (e.Status != GestureStatus.Running)
                return;
            var zoom = CoverCropMath.ClampZoom(_zoom * e.Scale);
            // zoom around the middle of the frame: the part shown there stays there
            var ratio = zoom / _zoom;
            _offsetX *= ratio;
            _offsetY *= ratio;
            _zoom = zoom;
            Apply();
        }

        // ------------------------------------------------------------------------------------------- show / close

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            _panel.Scale = 0.94;
            await Task.WhenAll(
                _scrim.FadeToAsync(0.45, 160, Easing.CubicOut),
                _panel.FadeToAsync(1, 160, Easing.CubicOut),
                _panel.ScaleToAsync(1, 200, Easing.CubicOut));
        }

        protected override bool OnBackButtonPressed()
        {
            Close(null);
            return true;
        }

        private async void Close(CropRegion? result)
        {
            if (_closing)
                return;
            _closing = true;
            try
            {
                await Task.WhenAll(_scrim.FadeToAsync(0, 120), _panel.FadeToAsync(0, 120));
                await Navigation.PopModalAsync(animated: false);
            }
            catch (Exception ex)
            {
                // the page is already gone (navigation changed underneath) - the answer still goes back
                System.Diagnostics.Debug.WriteLine($"[CoverCrop] Close failed: {ex.Message}");
            }
            finally
            {
                _result.TrySetResult(result);
            }
        }

        private static T Res<T>(string key) =>
            Application.Current is { } app && app.Resources.TryGetValue(key, out var value) && value is T typed
                ? typed
                : throw new InvalidOperationException($"Resource '{key}' is missing.");
    }

    /// <summary><see cref="ICoverCropper"/> over the topmost page (a modal, if one is open).</summary>
    public sealed class CoverCropper : ICoverCropper
    {
        public Task<CropRegion?> ChooseAsync(PickedPhoto photo) =>
            MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var shell = Shell.Current;
                var page = shell?.Navigation.ModalStack.LastOrDefault() ?? shell?.CurrentPage ?? Application.Current?.Windows.FirstOrDefault()?.Page;
                return page is null ? null : await CoverCropDialog.ShowAsync(page.Navigation, photo);
            });
    }
}
