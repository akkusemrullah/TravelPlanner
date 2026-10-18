namespace TravelPlanner.Helpers
{
    /// <summary>
    /// A short, self-dismissing notice ("Seyahatlerine eklendi"): an Android toast. Other platforms have no built-in
    /// toast - <see cref="TryShow"/> returns false there and the caller shows a message instead.
    /// </summary>
    public static class AppNotice
    {
        public static bool TryShow(string text)
        {
#if ANDROID
            MainThread.BeginInvokeOnMainThread(() =>
                Android.Widget.Toast.MakeText(Android.App.Application.Context, text, Android.Widget.ToastLength.Short)?.Show());
            return true;
#else
            return false;
#endif
        }
    }
}
