using System.Windows.Input;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>
/// TP-97: the boarding-pass trip card (see TripTicketView.xaml). The card's content binds to <see cref="Ticket"/>;
/// the hero buttons show for the commands that are set, side by side in equal columns.
/// </summary>
public partial class TripTicketView : ContentView
{
    public static readonly BindableProperty TicketProperty =
        BindableProperty.Create(nameof(Ticket), typeof(TripTicket), typeof(TripTicketView), propertyChanged: (b, _, n) => ((TripTicketView)b).Body.BindingContext = n);

    public static readonly BindableProperty WeatherCommandProperty =
        BindableProperty.Create(nameof(WeatherCommand), typeof(ICommand), typeof(TripTicketView), propertyChanged: OnButtonsChanged);

    public static readonly BindableProperty MapCommandProperty =
        BindableProperty.Create(nameof(MapCommand), typeof(ICommand), typeof(TripTicketView), propertyChanged: OnButtonsChanged);

    public static readonly BindableProperty DetailsCommandProperty =
        BindableProperty.Create(nameof(DetailsCommand), typeof(ICommand), typeof(TripTicketView), propertyChanged: OnButtonsChanged);

    public static readonly BindableProperty TapCommandProperty =
        BindableProperty.Create(nameof(TapCommand), typeof(ICommand), typeof(TripTicketView));

    public TripTicketView()
    {
        InitializeComponent();
        LayoutButtons();
    }

    public TripTicket? Ticket
    {
        get => (TripTicket?)GetValue(TicketProperty);
        set => SetValue(TicketProperty, value);
    }

    public ICommand? WeatherCommand
    {
        get => (ICommand?)GetValue(WeatherCommandProperty);
        set => SetValue(WeatherCommandProperty, value);
    }

    public ICommand? MapCommand
    {
        get => (ICommand?)GetValue(MapCommandProperty);
        set => SetValue(MapCommandProperty, value);
    }

    public ICommand? DetailsCommand
    {
        get => (ICommand?)GetValue(DetailsCommandProperty);
        set => SetValue(DetailsCommandProperty, value);
    }

    /// <summary>A tap anywhere on the card (the Dashboard opens the trip).</summary>
    public ICommand? TapCommand
    {
        get => (ICommand?)GetValue(TapCommandProperty);
        set => SetValue(TapCommandProperty, value);
    }

    private static void OnButtonsChanged(BindableObject bindable, object oldValue, object newValue) => ((TripTicketView)bindable).LayoutButtons();

    private void LayoutButtons()
    {
        var shown = new List<View>();
        foreach (var (button, command) in new (View, ICommand?)[] { (WeatherButton, WeatherCommand), (MapButton, MapCommand), (DetailsButton, DetailsCommand) })
        {
            button.IsVisible = command is not null;
            if (command is not null)
                shown.Add(button);
        }

        Buttons.ColumnDefinitions.Clear();
        for (var i = 0; i < shown.Count; i++)
        {
            Buttons.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(shown[i], i);
        }
        Buttons.IsVisible = shown.Count > 0;
    }
}
