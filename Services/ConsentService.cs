using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services
{
    /// <summary>The optional features that send data outside Türkiye and so need explicit consent (KVKK md. 9).</summary>
    public enum ConsentKind
    {
        /// <summary>AI trip planning: the planner's answers go to OpenAI (USA).</summary>
        AiPlanning,
        /// <summary>Sign in with Google: Google processes the sign-in and passes name, e-mail and photo.</summary>
        GoogleSignIn,
    }

    /// <summary>
    /// Explicit consent ("açık rıza", TP-25 - text in docs/legal/ACIK_RIZA.md and the in-app document "consent").
    /// Asked before the feature is first used, remembered on this device, asked again when the text's version
    /// changes, and can be withdrawn in Profil → Yasal ve gizlilik.
    /// </summary>
    public interface IConsentService
    {
        bool HasConsent(ConsentKind kind);

        /// <summary>True when consent is (or now gets) given; otherwise false and the feature must not run.</summary>
        Task<bool> EnsureAsync(ConsentKind kind);

        /// <summary>True when consent was given for anything (to offer withdrawing it).</summary>
        bool HasAnyConsent { get; }

        void RevokeAll();
    }

    public class ConsentService : IConsentService
    {
        /// <summary>Bump when the consent text changes in a way people must agree to again.</summary>
        public const string TextVersion = "2026-10-18";

        private readonly IPreferences _preferences;
        private readonly IDialogService _dialogs;

        public ConsentService(IPreferences preferences, IDialogService dialogs)
        {
            _preferences = preferences;
            _dialogs = dialogs;
        }

        internal static string Key(ConsentKind kind) => kind switch
        {
            ConsentKind.GoogleSignIn => "consent.google_sign_in",
            _ => "consent.ai_planning",
        };

        public bool HasConsent(ConsentKind kind) => _preferences.Get(Key(kind), string.Empty) == TextVersion;

        public bool HasAnyConsent => Enum.GetValues<ConsentKind>().Any(HasConsent);

        public async Task<bool> EnsureAsync(ConsentKind kind)
        {
            if (HasConsent(kind))
                return true;

            var (title, message) = kind switch
            {
                ConsentKind.GoogleSignIn => (AppResources.Consent_GoogleTitle, AppResources.Consent_GoogleMessage),
                _ => (AppResources.Consent_AiTitle, AppResources.Consent_AiMessage),
            };

            // An alert, not an action sheet: the whole text must be readable (an action sheet title can be cut off).
            if (!await _dialogs.ConfirmAsync(title, message, AppResources.Consent_Accept, AppResources.Consent_Decline))
                return false;

            _preferences.Set(Key(kind), TextVersion);
            return true;
        }

        public void RevokeAll()
        {
            foreach (var kind in Enum.GetValues<ConsentKind>())
                _preferences.Remove(Key(kind));
        }
    }
}
