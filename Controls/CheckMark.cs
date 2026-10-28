using Microsoft.Maui.Controls.Shapes;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// TP-103: the prototype's Checkbox - a 22 box with 6 corners and a 2 border (tp-border-strong), filled with the accent
    /// colour and a white tick when <see cref="IsChecked"/>. Display only: the row's own tap changes the value, so a
    /// recycled row never fires a "changed" event by itself (the platform CheckBox did, see TP-105).
    /// </summary>
    public class CheckMark : ContentView
    {
        public static readonly BindableProperty IsCheckedProperty = BindableProperty.Create(
            nameof(IsChecked), typeof(bool), typeof(CheckMark), false, propertyChanged: (b, _, _) => ((CheckMark)b).Update());

        public static readonly BindableProperty IsRoundProperty = BindableProperty.Create(
            nameof(IsRound), typeof(bool), typeof(CheckMark), false, propertyChanged: (b, _, _) => ((CheckMark)b).Update());

        private readonly Border _box;
        private readonly Label _tick;

        public CheckMark()
        {
            _tick = new Label
            {
                FontFamily = "Lucide",
                FontSize = 14,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.Fill, // TP-106: fill the tile + centred text, exact on Android
                VerticalOptions = LayoutOptions.Fill,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                Text = ListRow.Glyph("Ic.Check"),
            };
            _box = new Border
            {
                WidthRequest = 22,
                HeightRequest = 22,
                StrokeThickness = 2,
                Content = _tick,
            };
            Content = _box;
            InputTransparent = true;
            VerticalOptions = LayoutOptions.Center;
            Update();
        }

        public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }

        /// <summary>A round box (the prototype's "round" checkbox) instead of the square one.</summary>
        public bool IsRound { get => (bool)GetValue(IsRoundProperty); set => SetValue(IsRoundProperty, value); }

        private void Update()
        {
            _box.StrokeShape = new RoundRectangle { CornerRadius = IsRound ? 11 : 6 };
            _tick.IsVisible = IsChecked;
            _box.SetDynamicResource(Border.StrokeProperty, IsChecked ? "tp-accent" : "tp-border-strong");
            _box.SetDynamicResource(BackgroundColorProperty, IsChecked ? "tp-accent" : "tp-surface");
        }
    }
}
