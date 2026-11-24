using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Raven.Behaviors;

public static partial class LerpProgressBarBehavior
{
    public static readonly DependencyProperty TargetValueProperty =
        DependencyProperty.RegisterAttached(
            "TargetValue",
            typeof(double),
            typeof(LerpProgressBarBehavior),
            new PropertyMetadata(0d, OnTargetValueChanged)
        );

    public static void SetTargetValue(DependencyObject element, double value) =>
        element.SetValue(TargetValueProperty, value);

    public static double GetTargetValue(DependencyObject element) =>
        (double)element.GetValue(TargetValueProperty);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(State),
            typeof(LerpProgressBarBehavior),
            new PropertyMetadata(null)
        );

    private static State GetOrCreateState(ProgressBar bar)
    {
        var state = (State?)bar.GetValue(StateProperty);
        if (state != null)
            return state;

        state = new State(bar);
        bar.SetValue(StateProperty, state);

        // Clean up when unloaded - this is the key to stopping timers when page navigates away
        bar.Unloaded += (_, __) => state.Dispose();
        return state;
    }

    private static void OnTargetValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProgressBar bar)
            return;

        // Skip if not loaded - no point animating invisible elements
        if (!bar.IsLoaded)
            return;

        var state = GetOrCreateState(bar);
        state.SetTarget((double)e.NewValue);
    }

    private sealed partial class State : IDisposable
    {
        private readonly ProgressBar _bar;
        private DispatcherQueueTimer? _timer;
        private Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object>? _tickHandler;
        private double _displayed;
        private double _target;
        private long _lastTickMs;

        public State(ProgressBar bar)
        {
            _bar = bar;
            _displayed = bar.Value;
            _target = bar.Value;
        }

        public void SetTarget(double value)
        {
            _target = value;
            
            // If very close to target, snap directly without animation
            if (Math.Abs(_target - _displayed) < 0.5)
            {
                _displayed = _target;
                _bar.Value = _displayed;
                return;
            }

            // Start timer if needed
            EnsureStarted();
        }

        private void EnsureStarted()
        {
            if (_timer != null)
                return;

            _lastTickMs = Environment.TickCount64;
            
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null)
                return;
                
            _timer = dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(33); // ~30fps
            _tickHandler = (_, __) => Tick();
            _timer.Tick += _tickHandler;
            _timer.Start();
        }

        private void Tick()
        {
            // Safety check - stop if bar was unloaded
            if (!_bar.IsLoaded)
            {
                DisposeTimer();
                return;
            }
            
            var now = Environment.TickCount64;
            var dt = Math.Clamp((now - _lastTickMs) / 1000.0, 0, 0.1);
            _lastTickMs = now;

            const double speed = 15.0;
            var alpha = 1.0 - Math.Exp(-speed * dt);

            _displayed += (_target - _displayed) * alpha;

            // Snap when close
            if (Math.Abs(_target - _displayed) < 0.1)
                _displayed = _target;

            _bar.Value = _displayed;

            // Stop when settled
            if (Math.Abs(_target - _displayed) < 0.001)
            {
                DisposeTimer();
            }
        }

        private void DisposeTimer()
        {
            if (_timer == null)
                return;

            _timer.Stop();
            if (_tickHandler != null)
            {
                _timer.Tick -= _tickHandler;
                _tickHandler = null;
            }
            _timer = null;
        }

        public void Dispose() => DisposeTimer();
    }
}
