#if ANDROID
using AndroidX.AppCompat.App;
using Google.Android.Material.DatePicker;
#endif

namespace TravelPlanner.Services
{
    /// <inheritdoc cref="IDateRangePicker"/>
    public sealed class DateRangePicker : IDateRangePicker
    {
#if ANDROID
        public bool IsSupported => true;

        public Task<(DateTime Start, DateTime End)?> PickAsync(DateTime start, DateTime end, string title)
        {
            var result = new TaskCompletionSource<(DateTime Start, DateTime End)?>();
            if (Platform.CurrentActivity is not AppCompatActivity activity)
            {
                result.SetResult(null);
                return result.Task;
            }

            var picker = MaterialDatePicker.Builder.DateRangePicker()
                .SetTitleText(title)
                .SetSelection(new AndroidX.Core.Util.Pair(Java.Lang.Long.ValueOf(ToMillis(start)), Java.Lang.Long.ValueOf(ToMillis(end))))
                .Build();

            picker.AddOnPositiveButtonClickListener(new PositiveListener(selection =>
            {
                if (selection is AndroidX.Core.Util.Pair { First: Java.Lang.Long first, Second: Java.Lang.Long second })
                    result.TrySetResult((FromMillis(first.LongValue()), FromMillis(second.LongValue())));
                else
                    result.TrySetResult(null);
            }));
            // Positive clicks complete first; any other way out (back, X, outside) ends here.
            picker.AddOnDismissListener(new DismissListener(() => result.TrySetResult(null)));
            picker.Show(activity.SupportFragmentManager, "tp-date-range");

            return result.Task;
        }

        // The Material calendar works in UTC milliseconds at midnight of each day.
        private static long ToMillis(DateTime date) =>
            new DateTimeOffset(DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        private static DateTime FromMillis(long millis) =>
            DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime.Date, DateTimeKind.Unspecified);

        private sealed class PositiveListener(Action<Java.Lang.Object?> onClick) : Java.Lang.Object, IMaterialPickerOnPositiveButtonClickListener
        {
            public void OnPositiveButtonClick(Java.Lang.Object? selection) => onClick(selection);
        }

        private sealed class DismissListener(Action onDismiss) : Java.Lang.Object, Android.Content.IDialogInterfaceOnDismissListener
        {
            public void OnDismiss(Android.Content.IDialogInterface? dialog) => onDismiss();
        }
#else
        public bool IsSupported => false;

        public Task<(DateTime Start, DateTime End)?> PickAsync(DateTime start, DateTime end, string title) =>
            Task.FromResult<(DateTime Start, DateTime End)?>(null);
#endif
    }
}
