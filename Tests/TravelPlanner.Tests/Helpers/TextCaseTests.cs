using System.Globalization;
using Xunit;
using TravelPlanner.Helpers;

namespace TravelPlanner.Tests
{
    public class TextCaseTests
    {
        private static readonly CultureInfo Tr = new("tr-TR");

        [Theory]
        [InlineData("pasaport al", "Pasaport al")]
        [InlineData("istanbul gezisi", "İstanbul gezisi")]   // Turkish dotted İ
        [InlineData("ılgaz", "Ilgaz")]                       // Turkish dotless ı -> I
        [InlineData("  müze", "  Müze")]                     // leading spaces kept
        [InlineData("Roma", "Roma")]
        [InlineData("iPhone şarjı", "iPhone şarjı")]         // mixed-case word left alone
        [InlineData("3 gün", "3 gün")]                       // doesn't start with a letter
        [InlineData("", "")]
        [InlineData(null, null)]
        public void CapitalizeFirst_RaisesOnlyTheFirstLetter(string? input, string? expected) =>
            Assert.Equal(expected, TextCase.CapitalizeFirst(input, Tr));

        [Theory]
        [InlineData("new york", "New York")]
        [InlineData("emrullah akkuş", "Emrullah Akkuş")]
        [InlineData("birleşik  krallık", "Birleşik  Krallık")] // spacing kept as typed
        public void CapitalizeWords_RaisesEachWord(string input, string expected) =>
            Assert.Equal(expected, TextCase.CapitalizeWords(input, Tr));
    }
}
