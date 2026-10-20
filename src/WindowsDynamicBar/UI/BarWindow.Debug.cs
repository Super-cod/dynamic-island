#if DEBUG
using System.Linq;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Services.SystemInfo;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Debug builds only: commands sent over the single-instance pipe as "cmd:&lt;name&gt; [args]" so
/// scripted UI and soak tests can drive the island without moving the user's mouse. Not compiled
/// into Release builds.
/// </summary>
public partial class BarWindow
{
    internal void RunDebugCommand(string cmd)
    {
        string[] a = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0) return;
        Log.Info("Debug command", ("cmd", cmd));
        try
        {
            switch (a[0])
            {
                case "hover": OnIslandMouseEnter(this, null!); break;
                case "leave":
                    _islandHovering = false;
                    _pinned = false;
                    SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
                    break;
                case "metric": OnIconMetricClicked(IslandStateMachine.ParseMetric(a.ElementAtOrDefault(1))); break;
                case "collapse": SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true); break;
                case "search": OpenSearch(); break;
                case "search-close": CloseSearch(); break;
                case "type": SearchInput.Text = string.Join(' ', a.Skip(1)); break;
                case "gpu-next": OnGpuCycleRequested(); break;
                case "page": IconRow.SetPage(int.Parse(a[1]) - 1); break;
                case "next": IconRow.NextPage(); break;
                case "clip": OpenClipboardPanel(byHotkey: a.ElementAtOrDefault(1) == "focus"); break;
                case "clip-close": CloseClipboardPanel(); break;
                case "timer":
                    if (WindowsDynamicBar.Core.Timers.TimerCommand.TryParseDuration(string.Join(' ', a.Skip(1)), out var td))
                        OnTimerStartRequested(td);
                    break;
                case "timer-stop": OnTimerStopRequested(); break;
                case "battery-sim":
                    // "battery-sim 12 dc" simulates 12% on battery; "battery-sim off" goes back to real readings.
                    _battSim = a.ElementAtOrDefault(1) is { } v && v != "off"
                        ? (double.Parse(v, System.Globalization.CultureInfo.InvariantCulture), a.ElementAtOrDefault(2) == "ac")
                        : null;
                    ApplyBatteryInfo(App.Battery?.Current ?? new BatteryInfo(0, false, false, null, false));
                    break;
                case "focus": OnIdleFocusClick(this, new System.Windows.RoutedEventArgs()); break;
                case "search-enter":
                    if (_searchResults.Count > 0) ExecuteResult(_searchResults[Math.Max(0, _selectedResultIndex)]);
                    break;
                case "clip-search": ClipboardPanelComponent.DebugSetQuery(string.Join(' ', a.Skip(1))); break;
                case "clip-pin": if (ClipAt(a) is { } p) OnClipPin(p.Id, !p.Pinned); break;
                case "clip-del": if (ClipAt(a) is { } d) OnClipDelete(d.Id); break;
                case "clip-choose": if (ClipAt(a) is { } c) OnClipItemChosen(c); break;
                case "clip-count":
                    Log.Info("Debug clip", ("count", App.ClipHistory?.Count ?? -1), ("limit", App.ClipHistory?.Limit ?? -1));
                    break;
                case "toggle":
                    OnToggleRequested(Enum.Parse<WindowsDynamicBar.Services.Toggles.ToggleId>(a[1], ignoreCase: true),
                        a.ElementAtOrDefault(2) == "on");
                    break;
                case "state":
                    Log.Info("Debug state", ("island", _islandState), ("metric", _activeMetric), ("page", IconRow.Page + 1),
                        ("pinned", _pinned), ("w", Math.Round(_currentW)), ("h", Math.Round(_currentH)), ("tall", _tallWindow));
                    break;
                default: RunDebugCommandExtra(a); break;
            }
        }
        catch (Exception ex) { Log.Error("Debug command failed", ex); }
    }

    /// <summary>"clip-pin 2" -> the 2nd history item (1-based, newest first).</summary>
    private static WindowsDynamicBar.Core.Clipboard.ClipItem? ClipAt(string[] a)
    {
        var items = App.ClipHistory?.Items;
        int i = int.TryParse(a.ElementAtOrDefault(1), out int n) ? n - 1 : 0;
        return items is not null && i >= 0 && i < items.Count ? items[i] : null;
    }

    /// <summary>Feature commands added by later phases.</summary>
    partial void RunDebugCommandExtra(string[] args);
}
#endif
