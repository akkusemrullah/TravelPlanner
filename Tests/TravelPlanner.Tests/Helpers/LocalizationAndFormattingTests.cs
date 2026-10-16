using Moq;
using Xunit;
using System.Globalization;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Tests
{
    // MoneyFormatter.CurrencyCode is static, so tests that change it must not run in parallel with
    // others that format money.
    [CollectionDefinition("Currency", DisableParallelization = true)]
    public class CurrencyCollection { }

    [Collection("Currency")]
    public class MoneyFormatterTests : IDisposable
    {
        public void Dispose() => MoneyFormatter.CurrencyCode = MoneyFormatter.DefaultCurrencyCode;

        [Fact]
        public void FormatsInTheChosenCurrency_WithTheCurrentCulturesNumberFormat()
        {
            MoneyFormatter.CurrencyCode = "TRY";
            var lira = MoneyFormatter.Format(1234.56m, culture: new CultureInfo("tr-TR"));
            Assert.Contains("₺", lira);          // symbol position depends on the OS's ICU data
            Assert.Contains("1.234,56", lira);

            MoneyFormatter.CurrencyCode = "EUR";
            Assert.Equal("€1,234.56", MoneyFormatter.Format(1234.56m, culture: new CultureInfo("en-US")));
        }

        [Fact]
        public void Decimals_CanBeDropped_ForDashboardTotals()
        {
            MoneyFormatter.CurrencyCode = "USD";
            Assert.Equal("$1,235", MoneyFormatter.Format(1234.56m, decimals: 0, culture: new CultureInfo("en-US")));
        }

        [Fact]
        public void UnknownCurrency_FallsBackToTurkishLira()
        {
            MoneyFormatter.CurrencyCode = "XYZ";
            Assert.Equal("TRY", MoneyFormatter.CurrencyCode);
        }

        [Fact]
        public void CurrencyConverter_UsesMoneyFormatter()
        {
            MoneyFormatter.CurrencyCode = "GBP";
            var text = new CurrencyConverter().Convert(10m, typeof(string), null, CultureInfo.CurrentCulture);
            Assert.Equal(MoneyFormatter.Format(10m), text);
            Assert.StartsWith("£", (string)text);
        }
    }

    public class LocalizationTests
    {
        [Fact]
        public void EveryResourceKey_HasATurkishValue()
        {
            var keys = typeof(AppResources).GetProperties()
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => p.Name)
                .ToList();

            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(AppResources.ResourceManager.GetString(key, AppResources.Culture)), key));
        }

        [Fact]
        public void FormatStrings_HaveTheirPlaceholders()
        {
            Assert.Equal("Merhaba Emrullah", string.Format(AppResources.Dashboard_Greeting, "Emrullah"));
            Assert.Equal("5 Gün", string.Format(AppResources.Common_DaysFormat, 5));
        }

        [Theory]
        [InlineData("All", "Tümü")]
        [InlineData("Transportation", "Ulaşım")]
        [InlineData("Food", "Yemek")]
        [InlineData("Other", "Diğer")]
        public void ExpenseCategories_AreLocalized(string category, string expected)
        {
            Assert.Equal(expected, new ExpenseCategoryDisplayConverter().Convert(category, typeof(string), null, CultureInfo.CurrentCulture));
        }

        [Fact]
        public void VisitedConverter_IsLocalized()
        {
            var converter = new VisitedTextConverter();
            Assert.Equal("Ziyaret edildi", converter.Convert(true, typeof(string), null, CultureInfo.CurrentCulture));
            Assert.Equal("Ziyaret edilmedi", converter.Convert(false, typeof(string), null, CultureInfo.CurrentCulture));
        }

        [Fact]
        public void TripStatus_HasDisplayText()
        {
            Assert.Equal("Yaklaşan", TripStatus.Upcoming.ToDisplayText());
            Assert.Equal("Devam Ediyor", TripStatus.Ongoing.ToDisplayText());
            Assert.Equal("Tamamlandı", TripStatus.Completed.ToDisplayText());
        }
    }
}
