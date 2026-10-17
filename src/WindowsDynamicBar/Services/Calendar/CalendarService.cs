using System.Runtime.InteropServices;
using Windows.ApplicationModel.Appointments;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Interop;

namespace WindowsDynamicBar.Services.Calendar;

public sealed record CalendarEvent(string Title, DateTime Start);

/// <summary>
/// Read-only "next event today" from a real calendar. Sources, in order:
///  1. the Windows appointment store (WinRT; usually denied to unpackaged apps),
///  2. classic Outlook via COM/MAPI - only if Outlook is already running (never launched).
/// If no source answers, <see cref="HasCalendar"/> stays false and nothing is shown.
/// Events are never invented.
/// </summary>
public sealed class CalendarService
{
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(8);

    private readonly object _sync = new();
    private CalendarEvent? _next;
    private bool _hasCalendar;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _refreshing;

    /// <summary>Raised (on a worker thread) when a refresh finishes.</summary>
    public event Action? Changed;

    public bool HasCalendar { get { lock (_sync) return _hasCalendar; } }

    /// <summary>Next event that has not started yet and starts before midnight; null if none.</summary>
    public CalendarEvent? NextAfter(DateTime now)
    {
        lock (_sync)
        {
            if (_next is null || _next.Start <= now || _next.Start.Date != now.Date) return null;
            return _next;
        }
    }

    /// <summary>Kick a background refresh when the cached answer is older than <paramref name="maxAge"/>
    /// or the cached event has started. Cheap to call every tick.</summary>
    public void RefreshIfStale(TimeSpan maxAge)
    {
        DateTime now = DateTime.Now;
        lock (_sync)
        {
            if (_refreshing) return;
            bool expired = DateTime.UtcNow - _lastRefreshUtc > maxAge;
            bool passed = _next is not null && _next.Start <= now;
            if (!expired && !passed) return;
            _refreshing = true;
            _lastRefreshUtc = DateTime.UtcNow;
        }
        _ = Task.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        bool has = false;
        CalendarEvent? next = null;
        try
        {
            DateTime now = DateTime.Now;
            DateTime end = now.Date.AddDays(1);

            var (ok, ev) = await WithTimeout(QueryWindowsStoreAsync(now, end));
            if (!ok) (ok, ev) = await WithTimeout(Task.Run(() => QueryRunningOutlook(now, end)));
            has = ok;
            next = ev;
        }
        catch (Exception ex) { Log.Debug("Calendar refresh failed", ("error", ex.Message)); }
        finally
        {
            lock (_sync)
            {
                _hasCalendar = has;
                _next = next;
                _refreshing = false;
            }
        }
        try { Changed?.Invoke(); } catch { }
    }

    private static async Task<(bool ok, CalendarEvent? ev)> WithTimeout(Task<(bool ok, CalendarEvent? ev)> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(SourceTimeout));
        if (done != task) return (false, null);
        try { return await task; } catch { return (false, null); }
    }

    // ---------------------------------------------------------------- Windows appointment store

    private static async Task<(bool, CalendarEvent?)> QueryWindowsStoreAsync(DateTime now, DateTime end)
    {
        try
        {
            AppointmentStore? store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            if (store is null) return (false, null);

            var options = new FindAppointmentsOptions { MaxCount = 20 };
            options.FetchProperties.Add(AppointmentProperties.Subject);
            options.FetchProperties.Add(AppointmentProperties.StartTime);
            options.FetchProperties.Add(AppointmentProperties.AllDay);

            var items = await store.FindAppointmentsAsync(new DateTimeOffset(now), end - now, options);
            CalendarEvent? best = null;
            foreach (var a in items)
            {
                if (a.AllDay) continue;
                DateTime start = a.StartTime.LocalDateTime;
                if (start <= now || start >= end) continue;
                if (best is null || start < best.Start)
                    best = new CalendarEvent(string.IsNullOrWhiteSpace(a.Subject) ? "Event" : a.Subject.Trim(), start);
            }
            return (true, best);
        }
        catch (Exception ex)
        {
            Log.Debug("Windows calendar unavailable", ("error", ex.Message));
            return (false, null);
        }
    }

    // ---------------------------------------------------------------- classic Outlook (COM / MAPI)

    private static (bool, CalendarEvent?) QueryRunningOutlook(DateTime now, DateTime end)
    {
        if (NativeMethods.CLSIDFromProgID("Outlook.Application", out Guid clsid) != 0) return (false, null);
        if (NativeMethods.GetActiveObject(ref clsid, IntPtr.Zero, out object? app) != 0 || app is null) return (false, null);

        dynamic? ns = null, folder = null, items = null, restricted = null;
        try
        {
            dynamic outlook = app;
            ns = outlook.GetNamespace("MAPI");
            folder = ns.GetDefaultFolder(9); // olFolderCalendar
            items = folder.Items;
            items.IncludeRecurrences = true;
            items.Sort("[Start]");
            // Outlook parses Restrict dates in the user's locale ("g" = short date + short time).
            string filter = $"[Start] >= '{now:g}' AND [Start] < '{end:g}'";
            restricted = items.Restrict(filter);

            CalendarEvent? best = null;
            dynamic? item = restricted.GetFirst();
            for (int guard = 0; item is not null && guard < 50; guard++)
            {
                try
                {
                    bool allDay = item.AllDayEvent;
                    DateTime start = item.Start;
                    if (!allDay && start > now && start < end)
                    {
                        string subject = item.Subject ?? "";
                        best = new CalendarEvent(string.IsNullOrWhiteSpace(subject) ? "Event" : subject.Trim(), start);
                        break; // sorted by start
                    }
                }
                finally { Release(item); }
                item = restricted.GetNext();
            }
            return (true, best);
        }
        catch (Exception ex)
        {
            Log.Debug("Outlook calendar query failed", ("error", ex.Message));
            return (false, null);
        }
        finally
        {
            Release(restricted); Release(items); Release(folder); Release(ns); Release(app);
        }
    }

    private static void Release(object? o)
    {
        try { if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
    }
}
