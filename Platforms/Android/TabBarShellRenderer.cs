using Android.Content.Res;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Shape;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;

namespace TravelPlanner
{
    /// <summary>
    /// TP-99 / TP-101: the prototype's tab bar - a 1 px line on top, the selected tab's icon (22) on a soft blue
    /// 56x32 pill and the label (12, Inter) under the pill, never behind it. Colours still come from the Shell style
    /// in Styles.xaml; everything here is decoration, so any failure leaves the stock tab bar.
    /// </summary>
    internal sealed class TabBarShellRenderer : ShellRenderer
    {
        protected override IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) =>
            new PillTabBarTracker(this, shellItem);

        private sealed class PillTabBarTracker : ShellBottomNavViewAppearanceTracker
        {
            public PillTabBarTracker(IShellContext shellContext, ShellItem shellItem) : base(shellContext, shellItem)
            {
            }

            public override void SetAppearance(BottomNavigationView bottomView, IShellAppearanceElement appearance)
            {
                base.SetAppearance(bottomView, appearance);
                Apply(bottomView);
            }

            public override void ResetAppearance(BottomNavigationView bottomView)
            {
                base.ResetAppearance(bottomView);
                Apply(bottomView);
            }

            private static Color? Token(string key) =>
                Microsoft.Maui.Controls.Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value) && value is Color color
                    ? color
                    : null;

            private static void Apply(BottomNavigationView bottomView)
            {
                try
                {
                    var context = bottomView.Context!;
                    int Dp(double value) => (int)Math.Round(context.ToPixels(value));

                    // Taller bar so the pill and the label each get their own room (stock height is 56).
                    bottomView.SetMinimumHeight(Dp(70));
                    if (bottomView.LayoutParameters is { Height: > 0 } layout)
                    {
                        layout.Height = Dp(70);
                        bottomView.LayoutParameters = layout;
                    }
                    bottomView.Elevation = 0;
                    bottomView.ItemIconSize = Dp(22);
                    bottomView.ItemPaddingTop = Dp(9);
                    bottomView.ItemPaddingBottom = Dp(10);
                    bottomView.ActiveIndicatorLabelPadding = Dp(4);
                    bottomView.ItemTextAppearanceActive = Resource.Style.TpTabLabel;
                    bottomView.ItemTextAppearanceInactive = Resource.Style.TpTabLabel;

                    if (Token("tp-accent-soft") is { } pill)
                    {
                        bottomView.ItemActiveIndicatorEnabled = true;
                        bottomView.ItemActiveIndicatorColor = ColorStateList.ValueOf(pill.ToPlatform());
                        bottomView.ItemActiveIndicatorWidth = Dp(56);
                        bottomView.ItemActiveIndicatorHeight = Dp(32);
                        bottomView.ItemActiveIndicatorShapeAppearance = ShapeAppearanceModel.InvokeBuilder()
                            .SetAllCorners(CornerFamily.Rounded, Dp(16))
                            .Build();
                    }

                    // Surface with a 1 px border line on top (the prototype's border-t).
                    if (Token("tp-surface") is { } surface && Token("tp-border") is { } border)
                    {
                        var line = new ColorDrawable(border.ToPlatform());
                        var fill = new InsetDrawable(new ColorDrawable(surface.ToPlatform()), 0, Math.Max(1, Dp(1)), 0, 0);
                        bottomView.Background = new LayerDrawable(new Drawable[] { line, fill });
                    }

                    // Inter for the labels (the theme's font would be Roboto).
                    var fonts = IPlatformApplication.Current?.Services.GetService<IFontManager>();
                    var typeface = fonts?.GetTypeface(Microsoft.Maui.Font.OfSize("InterSemiBold", 12));
                    if (typeface is not null)
                        SetTypeface(bottomView, typeface);
                }
                catch (Exception ex)
                {
                    // only decoration: the tab bar works without it
                    System.Diagnostics.Debug.WriteLine($"[TabBar] Appearance: {ex.Message}");
                }
            }

            private static void SetTypeface(AView view, Android.Graphics.Typeface typeface)
            {
                if (view is TextView text)
                    text.Typeface = typeface;
                else if (view is ViewGroup group)
                    for (var i = 0; i < group.ChildCount; i++)
                        SetTypeface(group.GetChildAt(i)!, typeface);
            }
        }
    }
}
