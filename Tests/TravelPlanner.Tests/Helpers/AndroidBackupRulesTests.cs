using System.Xml.Linq;
using Xunit;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// TP-13: the unencrypted local data (database, attachments, users/ folders, SecureStorage preferences) must not
    /// leave the device through Android's Google backup or a device-to-device transfer.
    /// </summary>
    public class AndroidBackupRulesTests
    {
        private static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";
        private static readonly string[] Domains = { "root", "file", "database", "sharedpref", "external" };

        private static string AndroidFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TravelPlanner.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "Platforms", "Android");
        }

        [Fact]
        public void Manifest_TurnsBackupOff_AndPointsAtTheRules()
        {
            var application = XDocument.Load(Path.Combine(AndroidFolder(), "AndroidManifest.xml")).Root!.Element("application")!;

            Assert.Equal("false", (string?)application.Attribute(Android + "allowBackup"));
            Assert.Equal("@xml/data_extraction_rules", (string?)application.Attribute(Android + "dataExtractionRules"));
            Assert.Equal("@xml/backup_rules", (string?)application.Attribute(Android + "fullBackupContent"));
        }

        [Theory]
        [InlineData("cloud-backup")]
        [InlineData("device-transfer")]
        public void DataExtractionRules_ExcludeEverything(string section)
        {
            var rules = XDocument.Load(Path.Combine(AndroidFolder(), "Resources", "xml", "data_extraction_rules.xml")).Root!.Element(section)!;

            AssertExcludesEverything(rules);
        }

        [Fact]
        public void FullBackupContent_ExcludesEverything()
        {
            AssertExcludesEverything(XDocument.Load(Path.Combine(AndroidFolder(), "Resources", "xml", "backup_rules.xml")).Root!);
        }

        private static void AssertExcludesEverything(XElement rules)
        {
            Assert.Empty(rules.Elements("include"));
            foreach (var domain in Domains)
                Assert.Contains(rules.Elements("exclude"), e => (string?)e.Attribute("domain") == domain && (string?)e.Attribute("path") == ".");
        }
    }
}
