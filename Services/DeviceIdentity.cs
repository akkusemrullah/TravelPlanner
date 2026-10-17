using System.Security.Cryptography;
using System.Text;

namespace TravelPlanner.Services
{
    /// <summary>
    /// A stable, anonymous id of this phone for the AI planner's per-device daily quota: signing in with another
    /// account on the same phone doesn't give new plans. Only a salted SHA-256 hash leaves the device.
    /// It is only a helper signal: the server can't check it (any 64-character hex string passes). The real limits are
    /// the account quota, the lower quota of new accounts and the global daily ceiling (supabase migration 12, SEC-02).
    /// </summary>
    public interface IDeviceIdentity
    {
        /// <summary>64 lower-case hex characters.</summary>
        string Hash { get; }
    }

    public class DeviceIdentity : IDeviceIdentity
    {
        internal const string PreferenceKey = "device.install_id";
        private const string Salt = "TravelPlanner.ai-quota.v1:";

        private readonly IPreferences _preferences;
        private string? _hash;

        public DeviceIdentity(IPreferences preferences)
        {
            _preferences = preferences;
        }

        public string Hash => _hash ??= Compute(PlatformId() ?? InstallId());

        internal static string Compute(string rawId) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Salt + rawId))).ToLowerInvariant();

        /// <summary>Survives sign-in/out and app data being cleared (fallback for platforms without a device id).</summary>
        private string InstallId()
        {
            var id = _preferences.Get(PreferenceKey, string.Empty);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                _preferences.Set(PreferenceKey, id);
            }
            return id;
        }

        /// <summary>
        /// Android: Settings.Secure.ANDROID_ID - the same for this app on this phone even after reinstalling
        /// or clearing its data (it changes only with a factory reset).
        /// </summary>
        private static string? PlatformId()
        {
#if ANDROID
            try
            {
                var id = Android.Provider.Settings.Secure.GetString(
                    Android.App.Application.Context.ContentResolver, Android.Provider.Settings.Secure.AndroidId);
                return string.IsNullOrWhiteSpace(id) ? null : "android:" + id;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Device] ANDROID_ID unavailable: {ex.Message}");
            }
#endif
            return null;
        }
    }
}
