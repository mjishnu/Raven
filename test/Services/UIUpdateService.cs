using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;

namespace test.Services;

public sealed record UIUpdate(
    string? Status = null,
    double? Progress = null,
    string? Details = null
);

public sealed class UIUpdateService : INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcher;

    public DispatcherQueue DispatcherQueue => _dispatcher;

    public UIUpdateService(DispatcherQueue dispatcher) =>
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        private set
        {
            if (Math.Abs(_progress - value) > double.Epsilon)
            {
                _progress = value;
                OnPropertyChanged();
            }
        }
    }

    private string _detailsText = string.Empty;
    public string DetailsText
    {
        get => _detailsText;
        private set
        {
            if (_detailsText != value)
            {
                _detailsText = value;
                OnPropertyChanged();
            }
        }
    }

    // Convenience API for partial updates (no source parameter)
    public void SetStatus(string message) => EnqueueUI(() => StatusText = message);

    public void SetProgress(double value) => EnqueueUI(() => Progress = value);

    public void SetDetails(string details) => EnqueueUI(() => DetailsText = details);

    // Unified update (partial fields allowed)
    public void Update(UIUpdate update) =>
        EnqueueUI(() =>
        {
            // Avoid jank: when the animation is running, don't frequently overwrite StatusText
            // unless a new explicit Status payload is provided.
            if (update.Status is not null)
                StatusText = update.Status;
            if (update.Progress is double p)
                Progress = p;
            if (update.Details is not null)
                DetailsText = update.Details;
        });

    // Shared IProgress for helpers
    private IProgress<UIUpdate>? _reporter;

    public IProgress<UIUpdate> GetReporter() => _reporter ??= new Progress<UIUpdate>(Update);

    private void EnqueueUI(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    private DispatcherQueueTimer? _animTimer;
    private int _animDots = 0;
    private string _animBase = string.Empty;

    public bool IsStatusAnimationRunning => _animTimer is not null;

    public void StartStatusAnimation(string baseText, int intervalMs = 500)
    {
        EnqueueUI(() =>
        {
            _animBase = baseText;

            if (_animTimer is null)
            {
                _animDots = 0;
                _animTimer = _dispatcher.CreateTimer();
                _animTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
                _animTimer.Tick += AnimationTick;
                _animTimer.Start();
            }

            StatusText = ComposeAnimatedStatus();
        });
    }

    public void UpdateAnimatedStatusBase(string baseText)
    {
        EnqueueUI(() =>
        {
            if (_animTimer is null)
            {
                StatusText = baseText;
                return;
            }

            _animBase = baseText;
            StatusText = ComposeAnimatedStatus();
        });
    }

    public void StopStatusAnimation()
    {
        EnqueueUI(() =>
        {
            if (_animTimer is not null)
            {
                _animTimer.Stop();
                _animTimer.Tick -= AnimationTick;
                _animTimer = null;
            }

            _animDots = 0;
            _animBase = string.Empty;
        });
    }

    private void AnimationTick(object? sender, object? e)
    {
        _animDots = (_animDots % 3) + 1;
        StatusText = ComposeAnimatedStatus();
    }

    private string ComposeAnimatedStatus()
    {
        // Keep the string length constant to avoid layout re-measure/re-arrange jank.
        // Using a fixed 3-char tail prevents the status column from "shimmering".
        // Example: "Installing   ", "Installing.  ", "Installing.. ", "Installing..."
        var dots = _animDots switch
        {
            1 => ".  ",
            2 => ".. ",
            3 => "...",
            _ => "   "
        };

        return _animBase + dots;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
