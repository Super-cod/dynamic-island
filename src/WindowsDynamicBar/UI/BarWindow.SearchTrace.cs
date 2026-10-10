using System.Diagnostics;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Debug builds only: millisecond trace of the search bar open/close path (hotkey, layout,
/// animation start/settle, frame stalls) so first-open regressions can be read from the log.
/// Calls compile away in Release ([Conditional]).
/// </summary>
public partial class BarWindow
{
    private static readonly Stopwatch TraceClock = Stopwatch.StartNew();
    private long _traceLastFrameMs = -1;

    [Conditional("DEBUG")]
    private void SearchTrace(string evt, params (string Key, object? Value)[] props)
    {
        var all = new (string, object?)[props.Length + 5];
        all[0] = ("t", TraceClock.ElapsedMilliseconds);
        all[1] = ("state", _islandState);
        all[2] = ("w", Math.Round(_currentW, 1));
        all[3] = ("h", Math.Round(_currentH, 1));
        all[4] = ("top", Math.Round(_currentTop, 1));
        props.CopyTo(all, 5);
        Log.Info("[search-trace] " + evt, all);
    }

    /// <summary>Called on every applied animation frame; logs gaps longer than 3 frames.</summary>
    [Conditional("DEBUG")]
    private void SearchTraceFrame()
    {
        long now = TraceClock.ElapsedMilliseconds;
        if (_traceLastFrameMs >= 0 && now - _traceLastFrameMs > 50)
            SearchTrace("frame stall", ("gapMs", now - _traceLastFrameMs));
        _traceLastFrameMs = now;
    }
}
