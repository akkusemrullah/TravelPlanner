using Moq;
using Xunit;
using TravelPlanner.Helpers;

namespace TravelPlanner.Tests
{
    public class MoneyConverterTests
    {
        [Theory]
        [InlineData("0", 0L)]
        [InlineData("12.34", 1234L)]
        [InlineData("1999.99", 199999L)]
        [InlineData("0.005", 1L)]      // half a kuruş rounds away from zero
        [InlineData("-1.235", -124L)]
        public void ToMinor_RoundsToWholeMinorUnits(string amount, long expected)
        {
            Assert.Equal(expected, MoneyConverter.ToMinor(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
        }

        [Fact]
        public void FromMinor_IsExactInverse()
        {
            Assert.Equal(12.34m, MoneyConverter.FromMinor(1234));
            Assert.Equal(0.01m, MoneyConverter.FromMinor(1));
        }

        [Fact]
        public void RoundTrip_HasNoFloatingPointDrift()
        {
            // The bug TP-109 fixed: 0.1 + 0.2 stored through a REAL column came back as 0.30000000000000004.
            var total = MoneyConverter.FromMinor(MoneyConverter.ToMinor(0.1m) + MoneyConverter.ToMinor(0.2m));
            Assert.Equal(0.3m, total);
        }
    }
}
