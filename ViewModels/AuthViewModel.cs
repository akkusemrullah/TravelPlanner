using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    public enum AuthMode
    {
        SignIn,
        SignUp,
        /// <summary>Enter the code from the "Confirm signup" e-mail.</summary>
        VerifySignUp,
        /// <summary>Ask for a password reset code.</summary>
        ForgotPassword,
        /// <summary>Enter the reset code + the new password.</summary>
        ResetPassword
    }

    /// <summary>
    /// Sign in / create account / confirm code / reset password (G3), one page that switches between modes.
    /// Shown once at first start (the user may continue as a guest) and from Profile → "Giriş yap".
    /// Problems are shown inline under the form, not as dialogs.
    /// </summary>
    public partial class AuthViewModel : ViewModelBase
    {
        /// <summary>TP-15: new passwords (sign-up, reset): at least this long, with a letter and a digit.</summary>
        public const int MinPasswordLength = 8;

        /// <summary>Accounts created before TP-15 may still have a 6-character password - signing in accepts it.</summary>
        public const int LegacyMinPasswordLength = 6;
        public const int MaxDisplayNameLength = 40;

        private readonly IAuthService _auth;
        private readonly IAccountService _account;
        private readonly IConnectivity _connectivity;
        private readonly IConsentService? _consent;

        public AuthViewModel(IAuthService auth, IAccountService account, IConnectivity connectivity, IDialogService dialogs,
                             IConsentService? consent = null)
            : base(dialogs)
        {
            _consent = consent;
            _auth = auth;
            _account = account;
            _connectivity = connectivity;
        }

        /// <summary>Closes the page (Shell modal). Replaced in tests.</summary>
        internal Func<Task> CloseAction { get; set; } = async () =>
        {
            try
            {
                if (Shell.Current?.Navigation.ModalStack.Count > 0)
                    await Shell.Current.GoToAsync("..");
            }
            catch (Exception ex)
            {
                // The shell may be rebuilding after a sign-in (App.RebuildShellAsync) - nothing left to close.
                System.Diagnostics.Debug.WriteLine($"[Auth] Close: {ex.Message}");
            }
        };

        /// <summary>TP-105: Shell query that opens the page on a given form ("?mode=signup").</summary>
        public const string ModeQuery = "mode";
        public const string ModeSignUp = "signup";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSignIn), nameof(IsSignUp), nameof(IsVerifySignUp), nameof(IsForgotPassword),
                                  nameof(IsResetPassword), nameof(IsCodeMode), nameof(ShowEmailField), nameof(ShowPasswordField),
                                  nameof(ShowGoogle), nameof(Title), nameof(Subtitle), nameof(ShowClose), nameof(ShowBack))]
        private AuthMode mode = AuthMode.SignIn;

        [ObservableProperty]
        private string displayName = string.Empty;

        [ObservableProperty]
        private string email = string.Empty;

        [ObservableProperty]
        private string password = string.Empty;

        [ObservableProperty]
        private string code = string.Empty;

        [ObservableProperty]
        private string newPassword = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? errorMessage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasInfo))]
        private string? infoMessage;

        [ObservableProperty]
        private bool isWorking;

        public bool IsSignIn => Mode == AuthMode.SignIn;
        public bool IsSignUp => Mode == AuthMode.SignUp;
        public bool IsVerifySignUp => Mode == AuthMode.VerifySignUp;
        public bool IsForgotPassword => Mode == AuthMode.ForgotPassword;
        public bool IsResetPassword => Mode == AuthMode.ResetPassword;
        public bool IsCodeMode => Mode is AuthMode.VerifySignUp or AuthMode.ResetPassword;
        public bool ShowEmailField => Mode is AuthMode.SignIn or AuthMode.SignUp or AuthMode.ForgotPassword;
        public bool ShowPasswordField => Mode is AuthMode.SignIn or AuthMode.SignUp;
        public bool ShowGoogle => Mode is AuthMode.SignIn or AuthMode.SignUp;
        /// <summary>TP-101: sign in / create account close the page (✕); the code and reset steps go back (←).</summary>
        public bool ShowClose => Mode is AuthMode.SignIn or AuthMode.SignUp;

        public bool ShowBack => !ShowClose;

        /// <summary>TP-101: the eye button in the password field.</summary>
        [ObservableProperty]
        private bool isPasswordHidden = true;

        [RelayCommand]
        private void TogglePassword() => IsPasswordHidden = !IsPasswordHidden;

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public bool HasInfo => !string.IsNullOrEmpty(InfoMessage);

        public string Title => Mode switch
        {
            AuthMode.SignUp => AppResources.Auth_SignUpTitle,
            AuthMode.VerifySignUp => AppResources.Auth_VerifyTitle,
            AuthMode.ForgotPassword or AuthMode.ResetPassword => AppResources.Auth_ResetTitle,
            _ => AppResources.Auth_SignInTitle,
        };

        public string Subtitle => Mode switch
        {
            AuthMode.SignUp => AppResources.Auth_SignUpSubtitle,
            AuthMode.VerifySignUp => string.Format(AppResources.Auth_CodeSentFormat, Email.Trim()),
            AuthMode.ForgotPassword => AppResources.Auth_ForgotSubtitle,
            AuthMode.ResetPassword => string.Format(AppResources.Auth_CodeSentFormat, Email.Trim()),
            _ => AppResources.Auth_SignInSubtitle,
        };

        partial void OnEmailChanged(string value) => OnPropertyChanged(nameof(Subtitle));

        partial void OnModeChanged(AuthMode value)
        {
            ErrorMessage = null;
            InfoMessage = null;
            Code = string.Empty;
            NewPassword = string.Empty;
        }

        // ------------------------------------------------------------------ mode switches

        [RelayCommand]
        private void ShowSignIn() => Mode = AuthMode.SignIn;

        [RelayCommand]
        private void ShowSignUp() => Mode = AuthMode.SignUp;

        [RelayCommand]
        private void ShowForgotPassword() => Mode = AuthMode.ForgotPassword;

        /// <summary>"Misafir olarak devam et" / close: the app keeps working on this device's data only.</summary>
        [RelayCommand]
        private Task CloseAsync() => CloseAction();

        // ------------------------------------------------------------------ actions

        [RelayCommand]
        private Task SignInAsync() => RunAuthAsync(async () =>
        {
            if (!ValidateEmail() || !ValidateExistingPassword(Password))
                return;

            try
            {
                await _auth.SignInWithPasswordAsync(Email, Password);
            }
            catch (AuthException ex) when (ex.Kind == AuthErrorKind.EmailNotConfirmed)
            {
                // Signed up but never entered the code: send a fresh one and ask for it.
                await _auth.ResendSignUpCodeAsync(Email);
                Mode = AuthMode.VerifySignUp;
                InfoMessage = AppResources.Auth_NotConfirmedResent;
                return;
            }

            await FinishSignInAsync();
        });

        [RelayCommand]
        private Task SignUpAsync() => RunAuthAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                ErrorMessage = AppResources.Auth_NameRequired;
                return;
            }
            if (!ValidateEmail() || !ValidatePassword(Password))
                return;

            var name = Helpers.InputLimits.Truncate(DisplayName.Trim(), MaxDisplayNameLength)!;
            var outcome = await _auth.SignUpAsync(name, Email, Password);
            if (outcome == SignUpOutcome.SignedIn)
            {
                await FinishSignInAsync();
                return;
            }

            Mode = AuthMode.VerifySignUp;
        });

        [RelayCommand]
        private Task SignInWithGoogleAsync() => RunAuthAsync(async () =>
        {
            // TP-25: Google processes the sign-in abroad - explicit consent first (KVKK md. 9)
            if (_consent is not null && !await _consent.EnsureAsync(ConsentKind.GoogleSignIn))
                return;

            await _auth.SignInWithGoogleAsync();
            await FinishSignInAsync();
        });

        [RelayCommand]
        private Task VerifyCodeAsync() => RunAuthAsync(async () =>
        {
            if (!ValidateCode())
                return;

            await _auth.VerifySignUpCodeAsync(Email, Code);
            await FinishSignInAsync();
        });

        [RelayCommand]
        private Task ResendCodeAsync() => RunAuthAsync(async () =>
        {
            if (Mode == AuthMode.ResetPassword)
                await _auth.SendPasswordResetCodeAsync(Email);
            else
                await _auth.ResendSignUpCodeAsync(Email);

            InfoMessage = AppResources.Auth_CodeResent;
        });

        [RelayCommand]
        private Task SendResetCodeAsync() => RunAuthAsync(async () =>
        {
            if (!ValidateEmail())
                return;

            await _auth.SendPasswordResetCodeAsync(Email);
            Mode = AuthMode.ResetPassword;
        });

        [RelayCommand]
        private Task ResetPasswordAsync() => RunAuthAsync(async () =>
        {
            if (!ValidateCode() || !ValidatePassword(NewPassword))
                return;

            await _auth.ResetPasswordWithCodeAsync(Email, Code, NewPassword);
            await FinishSignInAsync();
        });

        private async Task FinishSignInAsync()
        {
            Password = string.Empty;
            NewPassword = string.Empty;
            await _account.ActivateSignedInAccountAsync();
            await CloseAction();
        }

        // ------------------------------------------------------------------ helpers

        private async Task RunAuthAsync(Func<Task> action)
        {
            if (IsWorking)
                return;

            ErrorMessage = null;
            InfoMessage = null;

            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
            {
                ErrorMessage = AppResources.Auth_Offline;
                return;
            }

            IsWorking = true;
            try
            {
                await action();
            }
            catch (AuthException ex) when (ex.Kind == AuthErrorKind.Cancelled)
            {
                // closed the browser - nothing to say
            }
            catch (AuthException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] {ex.Kind}: {ex.Message}");
                ErrorMessage = MessageFor(ex.Kind);
            }
            catch (Exception ex)
            {
                await Dialogs.ShowErrorAsync(AppResources.Common_GenericError, ex);
            }
            finally
            {
                IsWorking = false;
            }
        }

        internal static string MessageFor(AuthErrorKind kind) => kind switch
        {
            AuthErrorKind.Network => AppResources.Auth_ErrorNetwork,
            AuthErrorKind.InvalidCredentials => AppResources.Auth_ErrorInvalidCredentials,
            AuthErrorKind.EmailNotConfirmed => AppResources.Auth_ErrorNotConfirmed,
            AuthErrorKind.UserAlreadyExists => AppResources.Auth_ErrorUserExists,
            AuthErrorKind.WeakPassword => AppResources.Auth_ErrorWeakPassword,
            AuthErrorKind.InvalidEmail => AppResources.Auth_ErrorInvalidEmail,
            AuthErrorKind.InvalidCode => AppResources.Auth_ErrorInvalidCode,
            AuthErrorKind.RateLimited => AppResources.Auth_ErrorRateLimited,
            AuthErrorKind.NotSupported => AppResources.Auth_ErrorNotSupported,
            AuthErrorKind.Banned => AppResources.Auth_ErrorBanned,
            AuthErrorKind.CaptchaFailed => AppResources.Auth_ErrorCaptcha,
            AuthErrorKind.DisposableEmail => AppResources.Auth_ErrorDisposableEmail,
            AuthErrorKind.BrowserFailed => AppResources.Auth_ErrorBrowser,
            _ => AppResources.Auth_ErrorUnknown,
        };

        private bool ValidateEmail()
        {
            var value = Email.Trim();
            var at = value.IndexOf('@');
            if (at > 0 && at < value.Length - 3 && value.IndexOf('.', at) > at + 1 && !value.Contains(' '))
                return true;

            ErrorMessage = AppResources.Auth_ErrorInvalidEmail;
            return false;
        }

        /// <summary>TP-15: a new password - <see cref="MinPasswordLength"/>+ characters with at least one letter and one digit.</summary>
        private bool ValidatePassword(string value)
        {
            if (IsStrongEnough(value))
                return true;

            ErrorMessage = string.Format(AppResources.Auth_PasswordRulesFormat, MinPasswordLength);
            return false;
        }

        internal static bool IsStrongEnough(string? value) =>
            value is { Length: >= MinPasswordLength } && value.Any(char.IsLetter) && value.Any(char.IsDigit);

        /// <summary>Signing in: only rule out what no account can have (older accounts may have 6 characters).</summary>
        private bool ValidateExistingPassword(string value)
        {
            if ((value ?? string.Empty).Length >= LegacyMinPasswordLength)
                return true;

            ErrorMessage = string.Format(AppResources.Auth_PasswordTooShortFormat, LegacyMinPasswordLength);
            return false;
        }

        private bool ValidateCode()
        {
            var digits = new string((Code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
            if (digits.Length >= 6)
                return true;

            ErrorMessage = AppResources.Auth_ErrorInvalidCode;
            return false;
        }
    }
}
