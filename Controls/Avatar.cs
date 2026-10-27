using Microsoft.Maui.Controls.Shapes;
using TravelPlanner.Helpers;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// TP-104: the prototype's Avatar - a round tile with the person's initials, its soft colour picked from the name
    /// (<see cref="PersonName.ToneIndex"/>), so the same person looks the same in every list.
    /// </summary>
    public class Avatar : ContentView
    {
        public static readonly BindableProperty NameProperty = BindableProperty.Create(
            nameof(Name), typeof(string), typeof(Avatar), null, propertyChanged: (b, _, _) => ((Avatar)b).Update());

        public static readonly BindableProperty SizeProperty = BindableProperty.Create(
            nameof(Size), typeof(double), typeof(Avatar), 40d, propertyChanged: (b, _, _) => ((Avatar)b).UpdateSize());

        private readonly Border _circle;
        private readonly Label _initials;

        public Avatar()
        {
            _initials = new Label
            {
                FontFamily = "InterSemiBold",
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            _circle = new Border { StrokeThickness = 0, Stroke = Colors.Transparent, Padding = 0, Content = _initials };
            Content = _circle;
            AutomationProperties.SetIsInAccessibleTree(this, false);
            UpdateSize();
            Update();
        }

        public string? Name { get => (string?)GetValue(NameProperty); set => SetValue(NameProperty, value); }
        public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

        private void UpdateSize()
        {
            _circle.WidthRequest = Size;
            _circle.HeightRequest = Size;
            _circle.StrokeShape = new RoundRectangle { CornerRadius = Size / 2 };
            _initials.FontSize = Math.Round(Size * 0.36);
        }

        private void Update()
        {
            var initials = PersonName.Initials(Name);
            _initials.Text = initials.Length > 0 ? initials : "?";
            var tone = PersonName.AvatarTones[PersonName.ToneIndex(Name)];
            _circle.SetDynamicResource(BackgroundColorProperty, $"tp-{tone}-soft");
            _initials.SetDynamicResource(Label.TextColorProperty, tone == "accent" ? "tp-accent-text" : $"tp-{tone}");
        }
    }
}
