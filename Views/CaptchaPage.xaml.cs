using System.Globalization;
using TravelPlanner.Helpers;

namespace TravelPlanner.Views;

/// <summary>
/// Shows the Cloudflare Turnstile widget (<see cref="TurnstilePage"/>) and completes <see cref="Result"/> with its
/// token - or with null when the user cancels. Errors stay on the page with "Try again". Opened and closed by
/// <see cref="TurnstileCaptchaService"/>.
/// </summary>
public partial class CaptchaPage : ContentPage
{
    private readonly TaskCompletionSource<CaptchaResult?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDispatcherTimer? _poll;
    private bool _polling;

    public CaptchaPage()
    {
        InitializeComponent();
        Web.HandlerChanged += OnWebHandlerChanged;
        Load();
    }

    /// <summary>The token (IsToken) or null when cancelled.</summary>
    public Task<CaptchaResult?> Result => _result.Task;

    /// <summary>Gives up (the caller then closes the page).</summary>
    public void Cancel()
    {
        StopPolling();
        _result.TrySetResult(null);
    }

    private void Load()
    {
        ErrorBanner.IsVisible = false;
        RetryButton.IsVisible = false;
        Dots.IsVisible = true;

        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "tr";
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        Web.Source = new HtmlWebViewSource
        {
            Html = TurnstilePage.BuildHtml(SupabaseConfig.CaptchaSiteKey, language, dark),
            BaseUrl = TurnstilePage.BaseUrl,
        };
        StartPolling();
    }

    private void OnWebHandlerChanged(object? sender, EventArgs e)
    {
#if ANDROID
        // Turnstile runs in a cross-site iframe (challenges.cloudflare.com)
        if (Web.Handler?.PlatformView is Android.Webkit.WebView native)
            Android.Webkit.CookieManager.Instance?.SetAcceptThirdPartyCookies(native, true);
#endif
    }

    /// <summary>The page reports by navigating to TurnstilePage.ResultUrl: read it and stay on the page.</summary>
    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (TurnstilePage.FromNavigation(e.Url) is not { } result)
            return;
        e.Cancel = true;
        Handle(result);
    }

    /// <summary>Fallback in case a platform doesn't raise Navigating for the result URL: read window.tpResult.</summary>
    private void StartPolling()
    {
        StopPolling();
        _poll = Dispatcher.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(700);
        _poll.Tick += async (_, _) =>
        {
            if (_polling || _result.Task.IsCompleted)
                return;
            _polling = true;
            try
            {
                var value = await Web.EvaluateJavaScriptAsync(TurnstilePage.ReadResultScript);
                if (TurnstilePage.FromScript(value) is { } result)
                    Handle(result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Captcha] Reading the result failed: {ex.Message}");
            }
            finally
            {
                _polling = false;
            }
        };
        _poll.Start();
    }

    private void StopPolling()
    {
        _poll?.Stop();
        _poll = null;
    }

    private void Handle(CaptchaResult result)
    {
        if (_result.Task.IsCompleted || _poll is null)
            return; // already handled (Navigating and the poll can both see it)
        StopPolling();

        if (result.IsToken)
        {
            _result.TrySetResult(result);
            return;
        }

        // e.g. 110200 = this host isn't in the widget's Hostnames, 110100 = wrong site key, script_not_loaded = offline
        System.Diagnostics.Debug.WriteLine($"[Captcha] Turnstile error: {result.Error}");
        Dots.IsVisible = false;
        ErrorBanner.IsVisible = true;
        RetryButton.IsVisible = true;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => Load();

    private void OnCloseTapped(object? sender, TappedEventArgs e) => Cancel();

    private void OnCancelClicked(object? sender, EventArgs e) => Cancel();

    protected override bool OnBackButtonPressed()
    {
        Cancel();
        return true; // TurnstileCaptchaService closes the page
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Cancel(); // closed some other way: never leave the caller waiting
    }
}

/// <summary>
/// <see cref="Services.ICaptchaService"/> with Cloudflare Turnstile: shows <see cref="CaptchaPage"/> over the current
/// page and returns its token. No CAPTCHA (null) while <see cref="SupabaseConfig.CaptchaSiteKey"/> is empty.
/// </summary>
public sealed class TurnstileCaptchaService : Services.ICaptchaService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SupabaseConfig.CaptchaSiteKey))
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(() => ShowAsync(cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string?> ShowAsync(CancellationToken cancellationToken)
    {
        var navigation = Shell.Current?.Navigation ?? Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation
            ?? throw new Models.AuthException(Models.AuthErrorKind.CaptchaFailed, "No window to show the security check in.");

        var page = new CaptchaPage();
        CaptchaResult? result;
        try
        {
            await navigation.PushModalAsync(page);
            using (cancellationToken.Register(page.Cancel))
                result = await page.Result;
        }
        finally
        {
            try
            {
                if (navigation.ModalStack.Count > 0 && ReferenceEquals(navigation.ModalStack[^1], page))
                    await navigation.PopModalAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Captcha] Closing the page failed: {ex.Message}");
            }
        }

        if (result is not { IsToken: true } done)
            throw new Models.AuthException(Models.AuthErrorKind.Cancelled);
        return done.Token;
    }
}
