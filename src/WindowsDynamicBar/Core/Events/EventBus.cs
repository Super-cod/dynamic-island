using System.Windows.Threading;

namespace WindowsDynamicBar.Core.Events;

public enum BarEventKind
{
    None = 0,
    Media = 10,
    Volume = 20,
    Brightness = 25,
    Battery = 30,
    Network = 35,
    Bluetooth = 40,
    Download = 45,
    Screenshot = 50,
    Recording = 55,
    Privacy = 60,          // camera / microphone in use
    Clipboard = 65,
    Notification = 70,
    Timer = 75,
    Stopwatch = 76,
    Focus = 77,
    SystemMonitor = 80,
    Usb = 85,
    Toast = 90,            // generic informational (timer finished, download done…)
}

/// <summary>Immutable payload for a bar event. Never contains secrets.</summary>
public sealed record BarEvent(
    BarEventKind Kind,
    string Title,
    string? Subtitle = null,
    double? Value = null,          // 0..1 for progress/percentage displays
    string? ValueText = null,      // "72%", "00:43" …
    object? Payload = null,        // kind-specific data (e.g. MediaInfo, BitmapSource)
    int Priority = 0,              // higher wins when queued
    TimeSpan? DisplayDuration = null, // null = sticky until dismissed/timeout rules
    DateTimeOffset Timestamp = default,
    bool IsTick = false,           // ambient progress refresh: never pops the bar open
    bool Preempt = false)          // interactive HUD: takes over immediately from active cards
{
    public static BarEvent Create(BarEventKind kind, string title, string? subtitle = null,
        double? value = null, string? valueText = null, object? payload = null,
        int priority = 0, TimeSpan? displayDuration = null, bool isTick = false,
        bool preempt = false)
        => new(kind, title, subtitle, value, valueText, payload, priority, displayDuration,
            DateTimeOffset.Now, isTick, preempt);
}

public sealed class EventBus
{
    private readonly Dispatcher _dispatcher;
    private readonly List<BarEvent> _queue = new();
    private readonly object _sync = new();

    /// <summary>Raised on the UI thread whenever an event should be shown or state changed.</summary>
    public event Action<BarEvent>? EventRaised;

    /// <summary>Raised (UI thread) when all transient events have expired and nothing is active.</summary>
    public event Action? Idle;

    public BarEvent? Active { get; private set; }
    private DispatcherTimer? _expiryTimer;
    private DateTime _activeShownAt;

    public EventBus(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Raise(BarEvent evt)
    {
        // Everything (including the Active read) happens on the UI thread — services
        // raise from background threads, so a single marshal keeps state consistent.
        _dispatcher.Invoke(() =>
        {
            if (Active?.Kind == evt.Kind)
            {
                // Coalesce: same kind refreshes in place (e.g. volume drag).
                Active = evt;
                _activeShownAt = DateTime.UtcNow;
                EventRaised?.Invoke(evt);
                ArmExpiry(evt);
                return;
            }

            // Preemption: interactive HUDs (Volume, Brightness) take over immediately from ambient cards
            if (evt.Preempt && Active is not null && Active.Kind != evt.Kind)
            {
                lock (_sync)
                {
                    if (Active.DisplayDuration is null || DateTime.UtcNow - _activeShownAt < Active.DisplayDuration.Value)
                    {
                        var old = Active;
                        if (!_queue.Any(e => e.Kind == old.Kind))
                            _queue.Add(old);
                    }
                }
                Active = null;
            }

            lock (_sync)
            {
                var existing = _queue.FirstOrDefault(e => e.Kind == evt.Kind);
                if (existing is not null) _queue.Remove(existing);
                _queue.Add(evt);
            }
            PumpOnDispatcher();
        });
    }

    /// <summary>Dismiss current event immediately (click outside / Escape).</summary>
    public void Dismiss()
    {
        _dispatcher.Invoke(() =>
        {
            Active = null;
            _expiryTimer?.Stop();
            PumpOnDispatcher();
        });
    }

    private void Pump() => _dispatcher.Invoke(PumpOnDispatcher);

    private void PumpOnDispatcher()
    {
        if (Active is not null && (Active.DisplayDuration is null ||
            DateTime.UtcNow - _activeShownAt < Active.DisplayDuration.Value))
            return;

        BarEvent? next = null;
        lock (_sync)
        {
            if (_queue.Count > 0)
            {
                next = _queue.OrderByDescending(e => e.Priority)
                             .ThenByDescending(e => e.Timestamp)
                             .First();
                _queue.Remove(next);
            }
        }

        if (next is not null)
        {
            Active = next;
            _activeShownAt = DateTime.UtcNow;
            EventRaised?.Invoke(next);
            ArmExpiry(next);
        }
        else
        {
            Active = null;
            Idle?.Invoke();
        }
    }

    private void ArmExpiry(BarEvent evt)
    {
        _expiryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _expiryTimer.Tick -= OnExpiryTick;
        _expiryTimer.Tick += OnExpiryTick;
        _expiryTimer.Stop();
        _expiryTimer.Start();
    }

    private void OnExpiryTick(object? sender, EventArgs e)
    {
        if (Active is null) { _expiryTimer?.Stop(); return; }
        var duration = Active.DisplayDuration ?? TimeSpan.FromSeconds(4);
        if (DateTime.UtcNow - _activeShownAt >= duration)
        {
            Active = null;
            _expiryTimer?.Stop();
            Pump();
        }
    }
}
