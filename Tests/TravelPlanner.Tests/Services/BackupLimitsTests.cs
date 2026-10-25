using Xunit;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    /// <summary>TP-72: real byte limits on restore, and no backup copies left in the cache.</summary>
    public class BackupLimitsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "tp-bak-limits-" + Guid.NewGuid().ToString("N"));

        public BackupLimitsTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        [Fact]
        public void LimitedReadStream_FailsOnceMoreComesOutThanAllowed()
        {
            using var ok = new BackupService.LimitedReadStream(new MemoryStream(new byte[100]), 100);
            ok.CopyTo(Stream.Null);

            using var tooMuch = new BackupService.LimitedReadStream(new MemoryStream(new byte[101]), 100);
            Assert.Throws<InvalidBackupException>(() => tooMuch.CopyTo(Stream.Null));
        }

        [Fact]
        public void DeleteOldExports_RemovesOnlyOldBackupFiles()
        {
            var old = Path.Combine(_dir, "TravelPlanner-2026-10-01-1200.zip");
            var fresh = Path.Combine(_dir, "TravelPlanner-2026-10-25-1200.zip");
            var leftover = Path.Combine(_dir, "restore-abc.tmp");
            var other = Path.Combine(_dir, "cover.jpg");
            foreach (var file in new[] { old, fresh, leftover, other })
                File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));

            Assert.Equal(2, BackupService.DeleteOldExports(_dir, TimeSpan.FromHours(1)));

            Assert.False(File.Exists(old));
            Assert.False(File.Exists(leftover));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(other));
        }
    }
}
