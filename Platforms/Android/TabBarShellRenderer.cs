using Android.Content.Res;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Shape;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Platform;

namespace TravelPlanner
{
    /// <summary>
    /// TP-99: the prototype's tab bar - the selected tab's icon sits on a soft blue pill (Material's active
    /// indicator). Everything else (colours, titles) still comes from the Shell style in Styles.xaml.
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
                ApplyPill(bottomView);
            }

            public override void ResetAppearance(BottomNavigationView bottomView)
            {
                base.ResetAppearance(bottomView);
                ApplyPill(bottomView);
            }

            private static void ApplyPill(BottomNavigationView bottomView)
            {
                try
                {
                    var resources = Microsoft.Maui.Controls.Application.Current?.Resources;
                    if (resources is null || !resources.TryGetValue("tp-accent-soft", out var value) || value is not Color color)
                        return;

                    var context = bottomView.Context!;
                    var width = (int)context.ToPixels(64);
                    var height = (int)context.ToPixels(32);

                    bottomView.ItemActiveIndicatorEnabled = true;
                    bottomView.ItemActiveIndicatorColor = ColorStateList.ValueOf(color.ToPlatform());
                    bottomView.ItemActiveIndicatorWidth = width;
                    bottomView.ItemActiveIndicatorHeight = height;
                    bottomView.ItemActiveIndicatorShapeAppearance = ShapeAppearanceModel.InvokeBuilder()
                        .SetAllCorners(CornerFamily.Rounded, height / 2f)
                        .Build();
                }
                catch (Exception ex)
                {
                    // only decoration: the tab bar works without the pill
                    System.Diagnostics.Debug.WriteLine($"[TabBar] Active indicator: {ex.Message}");
                }
            }
        }
    }
}
