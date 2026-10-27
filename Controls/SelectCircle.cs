using Microsoft.Maui.Controls.Shapes;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// TP-102: the prototype's SelectCircle in "Seçerek sil" mode - a 24 ring (tp-border-strong) that turns into a red
    /// disc with a white tick when <see cref="IsOn"/>. Display only: the row's own tap toggles the selection.
    /// </summary>
    public class SelectCircle : ContentView
    {
        public static readonly BindableProperty IsOnProperty = BindableProperty.Create(
            nameof(IsOn), typeof(bool), typeof(SelectCircle), false, propertyChanged: (b, _, _) => ((SelectCircle)b).Update());

        private readonly Border _ring;
        private readonly Label _tick;

        public SelectCircle()
        {
            _tick = new Label
            {
                FontFamily = "Lucide",
                FontSize = 14,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Text = ListRow.Glyph("Ic.Check"),
            };
            _ring = new Border
            {
                WidthRequest = 24,
                HeightRequest = 24,
                StrokeThickness = 2,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Content = _tick,
            };
            Content = _ring;
            InputTransparent = true;
            VerticalOptions = LayoutOptions.Center;
            Update();
        }

        public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

        private void Update()
        {
            _tick.IsVisible = IsOn;
            _ring.SetDynamicResource(Border.StrokeProperty, IsOn ? "tp-danger" : "tp-border-strong");
            _ring.SetDynamicResource(BackgroundColorProperty, IsOn ? "tp-danger" : "tp-surface");
        }
    }
}
