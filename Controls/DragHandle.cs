namespace TravelPlanner.Controls
{
    /// <summary>How far the finger has moved since the drag started (device-independent units, + = down).</summary>
    public sealed class DragDeltaEventArgs : EventArgs
    {
        public DragDeltaEventArgs(double totalY)
        {
            TotalY = totalY;
        }

        public double TotalY { get; }
    }

    /// <summary>
    /// The "⋮⋮" grip on a list row: press it and move up/down to reorder (day plan, TP-401). It only reports the
    /// vertical movement; the page decides what to move.
    /// - Android: a custom handler (Platforms/Android/DragHandleHandler.cs) tracks the touch itself, measured on the
    ///   screen (so moving rows never make it jitter) and tells the surrounding list / swipe view / pull-to-refresh
    ///   not to take the gesture over.
    /// - Other platforms: a normal PanGestureRecognizer.
    /// </summary>
    public class DragHandle : ContentView
    {
        public event EventHandler? DragStarted;

        public event EventHandler<DragDeltaEventArgs>? DragMoved;

        /// <summary>The finger was lifted or the gesture was cancelled.</summary>
        public event EventHandler? DragEnded;

        public DragHandle()
        {
            BackgroundColor = Colors.Transparent;
#if !ANDROID
            var pan = new PanGestureRecognizer();
            pan.PanUpdated += OnPanUpdated;
            GestureRecognizers.Add(pan);
#endif
        }

#if !ANDROID
        private bool _panning;

        private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _panning = true;
                    RaiseStarted();
                    break;
                case GestureStatus.Running:
                    if (_panning)
                        RaiseMoved(e.TotalY);
                    break;
                default:
                    if (_panning)
                    {
                        _panning = false;
                        RaiseEnded();
                    }
                    break;
            }
        }
#endif

        internal void RaiseStarted() => DragStarted?.Invoke(this, EventArgs.Empty);

        internal void RaiseMoved(double totalY) => DragMoved?.Invoke(this, new DragDeltaEventArgs(totalY));

        internal void RaiseEnded() => DragEnded?.Invoke(this, EventArgs.Empty);
    }
}
