namespace TravelPlanner.Models
{
    public class WeatherInfo
    {
        public double Temperature { get; set; }
        public string Condition { get; set; } = string.Empty;
        public int Humidity { get; set; }
        public double WindSpeed { get; set; }

        /// <summary>Emoji matching the current WMO weather code, for a quick-glance icon in the UI.</summary>
        public string IconEmoji { get; set; } = "🌡️";
    }
}
