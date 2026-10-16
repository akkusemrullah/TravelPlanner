using Android.Content;
using Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using TravelPlanner.Controls;

namespace TravelPlanner
{
    /// <summary>
    /// Android side of <see cref="DragHandle"/>. MAUI's PanGestureRecognizer is unreliable here: inside a
    /// CollectionView / RefreshView the list takes a vertical drag over (the pan is cancelled), and its
    /// coordinates are relative to the moving row, which makes a dragged row jitter. This view handles the touch
    /// itself instead: on the first touch it asks every parent not to intercept the gesture, and it measures the
    /// movement on the screen (RawY), so the rows can be reordered under the finger.
    /// </summary>
    internal sealed class DragHandleHandler : ContentViewHandler
    {
        protected override ContentViewGroup CreatePlatformView()
        {
            var view = new DragHandleView(Context, this) { CrossPlatformLayout = VirtualView };
            view.SetClipChildren(false);
            return view;
        }

        private sealed class DragHandleView : ContentViewGroup
        {
            private readonly DragHandleHandler _handler;
            private float _startRawY;
            private bool _dragging;

            public DragHandleView(Context context, DragHandleHandler handler)
                : base(context)
            {
                _handler = handler;
            }

            // The grip icon inside must not swallow the touch - the whole handle is one touch target.
            public override bool OnInterceptTouchEvent(MotionEvent? ev) => true;

            public override bool OnTouchEvent(MotionEvent? e)
            {
                if (e is null || _handler.VirtualView is not DragHandle handle)
                    return base.OnTouchEvent(e);

                switch (e.ActionMasked)
                {
                    case MotionEventActions.Down:
                        // Keeps the RecyclerView (scroll) and RefreshView (pull to refresh)
                        // from taking this gesture over until the finger is lifted.
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        _startRawY = e.RawY;
                        _dragging = true;
                        handle.RaiseStarted();
                        return true;

                    case MotionEventActions.Move:
                        if (_dragging)
                            handle.RaiseMoved(Context.FromPixels(e.RawY - _startRawY));
                        return true;

                    case MotionEventActions.Up:
                    case MotionEventActions.Cancel:
                        if (_dragging)
                        {
                            _dragging = false;
                            Parent?.RequestDisallowInterceptTouchEvent(false);
                            handle.RaiseEnded();
                        }
                        return true;

                    default:
                        return true;
                }
            }
        }
    }
}
