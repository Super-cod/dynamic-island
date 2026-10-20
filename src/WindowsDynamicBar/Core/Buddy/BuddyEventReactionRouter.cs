using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.UI.Buddy;

namespace WindowsDynamicBar.Core.Buddy;

public sealed class BuddyEventReactionRouter : IDisposable
{
    private readonly EventBus _bus;
    private readonly BuddyController _controller;
    private readonly Func<SettingsModel> _getSettings;
    private readonly Dictionary<string, DateTime> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTime> _eventTimestamps = new();
    private DateTime _stormQuietUntil = DateTime.MinValue;
    private bool _stormDizzyPlayed;
    private DateTime _resumeQuietUntil = DateTime.MinValue;
    private bool _pendingResumeStretch;
    private DateTime _lastStatusSpokenTime = DateTime.MinValue;
    private string? _pendingStatusLine;
    private string? _lastSpokenCapsuleText;
    private DispatcherTimer? _wifiDisconnectDebounce;
    private DateTime _lastOfflineStartTime = DateTime.MinValue;
    private bool _hasSaidBackOnline;
    private bool _batteryLowAlertFired;
    private bool _vpnDisconnectFired;
    private bool _micInUse;
    private bool _cameraInUse;
    private bool _callActive;
    private bool _isDnd;

    public void ResetStorm()
    {
        _eventTimestamps.Clear();
        _stormQuietUntil = DateTime.MinValue;
        _stormDizzyPlayed = false;
    }

    public BuddyEventReactionRouter(EventBus bus, BuddyController controller, Func<SettingsModel> getSettings)
    {
        _bus = bus;
        _controller = controller;
        _getSettings = getSettings;

        _bus.EventRaised += OnBusEventRaised;
        _bus.Idle += OnBusIdle;
    }

    public void OnSystemResumedFromSleep(DateTime now)
    {
        _resumeQuietUntil = now.AddSeconds(0.5);
        _pendingResumeStretch = true;
    }

    public void UpdateTick(DateTime now)
    {
        if (_pendingResumeStretch && now >= _resumeQuietUntil)
        {
            _pendingResumeStretch = false;
            var s = _getSettings();
            if (s.FeatureBuddy && s.BuddyReactionsEnabled)
            {
                _controller.Behavior.TriggerEvent(
                    2,
                    BuddyAnimationState.Stretching,
                    BuddyMood.Sleepy,
                    BuddyAccessory.None,
                    3.5
                );
            }
        }
    }

    private bool CheckCooldown(string key, TimeSpan cooldown, DateTime now)
    {
        if (_cooldowns.TryGetValue(key, out var expiry) && now < expiry)
        {
            return false;
        }
        _cooldowns[key] = now.Add(cooldown);
        return true;
    }

    private void OnBusEventRaised(BarEvent evt)
    {
        var s = _getSettings();
        if (!s.FeatureBuddy || !s.BuddyReactionsEnabled) return;

        DateTime now = DateTime.UtcNow;

        _eventTimestamps.Enqueue(now);
        while (_eventTimestamps.Count > 0 && (now - _eventTimestamps.Peek()).TotalSeconds > 10.0)
        {
            _eventTimestamps.Dequeue();
        }

        if (_eventTimestamps.Count >= 12)
        {
            if (!_stormDizzyPlayed)
            {
                _stormDizzyPlayed = true;
                _stormQuietUntil = now.AddSeconds(15.0);
                _controller.Behavior.TriggerEvent(
                    3,
                    BuddyAnimationState.Dizzy,
                    BuddyMood.Curious,
                    BuddyAccessory.DizzyStars,
                    3.5
                );
            }
            return;
        }

        if (now < _stormQuietUntil) return;
        _stormDizzyPlayed = false;

        if (now < _resumeQuietUntil) return;

        if (s.BuddyQuietDuringCalls && _callActive) return;

        if (_isDnd)
        {
            // Only critical battery <= 10% can interrupt DND!
            bool isCriticalBattery = evt.Kind == BarEventKind.Battery &&
                ((evt.Value.HasValue && evt.Value.Value <= 0.10) ||
                 (evt.ValueText?.Contains("10%") == true) ||
                 (evt.ValueText?.Contains("8%") == true) ||
                 (evt.ValueText?.Contains("5%") == true));
            if (!isCriticalBattery) return;
        }

        uint eventColor = GetEventColor(evt.Kind);
        _controller.Sprite.PeekAntennaColor = eventColor;

        DispatchReaction(evt, s, now);
    }

    private void DispatchReaction(BarEvent evt, SettingsModel s, DateTime now)
    {
        switch (evt.Kind)
        {
            case BarEventKind.Screenshot:
                if (!s.BuddyReactScreenshot) return;
                HandleScreenshot(evt, now);
                break;
            case BarEventKind.Network:
                if (!s.BuddyReactWifi && !s.BuddyReactVpn && !s.BuddyReactAirplane) return;
                HandleNetwork(evt, s, now);
                break;
            case BarEventKind.Bluetooth:
                if (!s.BuddyReactBluetooth) return;
                HandleBluetooth(evt, now);
                break;
            case BarEventKind.Battery:
                if (!s.BuddyReactPower) return;
                HandleBattery(evt, now);
                break;
            case BarEventKind.Volume:
                if (!s.BuddyReactAudioOutput) return;
                HandleAudioOutput(evt, now);
                break;
            case BarEventKind.Privacy:
                if (!s.BuddyReactPrivacy) return;
                HandlePrivacy(evt, now);
                break;
            case BarEventKind.Clipboard:
                if (!s.BuddyReactClipboard) return;
                HandleClipboard(evt, now);
                break;
            case BarEventKind.Notification:
                if (evt.Title?.Contains("call", StringComparison.OrdinalIgnoreCase) == true
                    || evt.Subtitle?.Contains("call", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _callActive = true;
                }
                break;
        }
    }

    private void HandleScreenshot(BarEvent evt, DateTime now)
    {
        if (!CheckCooldown("screenshot", TimeSpan.FromSeconds(0.2), now)) return;
        bool isFailure = evt.Subtitle?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true
            || evt.Title?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true;

        if (isFailure)
        {
            _controller.Behavior.TriggerEvent(2, BuddyAnimationState.Shrug, BuddyMood.Sad, BuddyAccessory.None, 2.0);
        }
        else
        {
            _controller.Behavior.TriggerEvent(2, BuddyAnimationState.PhotoReview, BuddyMood.Happy, BuddyAccessory.Camera, 2.5);
            QueueSpokenLine("Got your screenshot!", evt.Title, now);
        }
    }

    private void HandleNetwork(BarEvent evt, SettingsModel s, DateTime now)
    {
        string text = (evt.Title + " " + (evt.Subtitle ?? "")).ToLowerInvariant();

        if (text.Contains("vpn"))
        {
            if (!s.BuddyReactVpn) return;
            bool connected = !text.Contains("disconnect") && !text.Contains("off");
            if (connected)
            {
                if (CheckCooldown("vpn_on", TimeSpan.FromSeconds(0.2), now))
                {
                    _controller.Behavior.TriggerEvent(2, BuddyAnimationState.StandTaller, BuddyMood.Cool, BuddyAccessory.ShieldCheck, 3.5);
                }
            }
            else
            {
                if (!_vpnDisconnectFired && CheckCooldown("vpn_off", TimeSpan.FromSeconds(0.2), now))
                {
                    _vpnDisconnectFired = true;
                    _controller.Behavior.TriggerEvent(3, BuddyAnimationState.Surprise, BuddyMood.Surprised, BuddyAccessory.Exclamation, 3.0);
                }
            }
            return;
        }

        if (text.Contains("airplane") || text.Contains("flight"))
        {
            if (!s.BuddyReactAirplane) return;
            bool airplaneOn = text.Contains("on") || !text.Contains("off");
            if (CheckCooldown("airplane", TimeSpan.FromSeconds(0.2), now))
            {
                if (airplaneOn)
                {
                    _controller.Behavior.TriggerEvent(2, BuddyAnimationState.GlidePlane, BuddyMood.Calm, BuddyAccessory.Plane, 4.0);
                }
                else
                {
                    _controller.Behavior.TriggerEvent(2, BuddyAnimationState.Hopping, BuddyMood.Happy, BuddyAccessory.None, 2.0);
                }
            }
            return;
        }

        if (!s.BuddyReactWifi) return;
        bool wifiOnline = !text.Contains("disconnect") && !text.Contains("offline") && !text.Contains("no internet");

        if (wifiOnline)
        {
            if (CheckCooldown("wifi_connected", TimeSpan.FromSeconds(0.2), now))
            {
                _controller.Behavior.TriggerEvent(2, BuddyAnimationState.Hopping, BuddyMood.Happy, BuddyAccessory.WifiArcs, 3.0);
                if (_lastOfflineStartTime != DateTime.MinValue && (now - _lastOfflineStartTime).TotalMinutes >= 2.0 && !_hasSaidBackOnline)
                {
                    _hasSaidBackOnline = true;
                    QueueSpokenLine("We're back online!", evt.Title, now);
                }
                _lastOfflineStartTime = DateTime.MinValue;
            }
        }
        else
        {
            if (_lastOfflineStartTime == DateTime.MinValue) _lastOfflineStartTime = now;
            _hasSaidBackOnline = false;

            _wifiDisconnectDebounce?.Stop();
            _wifiDisconnectDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.2) };
            _wifiDisconnectDebounce.Tick += (_, _) =>
            {
                _wifiDisconnectDebounce.Stop();
                if (CheckCooldown("wifi_disconnected", TimeSpan.FromSeconds(0.2), DateTime.UtcNow))
                {
                    _controller.Behavior.TriggerEvent(3, BuddyAnimationState.ScratchHead, BuddyMood.Sad, BuddyAccessory.Question, 4.0);
                }
            };
            _wifiDisconnectDebounce.Start();
        }
    }

    private void HandleBluetooth(BarEvent evt, DateTime now)
    {
        string text = (evt.Title + " " + (evt.Subtitle ?? "")).ToLowerInvariant();

        if (text.Contains("off") || text.Contains("disabled"))
        {
            if (CheckCooldown("bt_off", TimeSpan.FromSeconds(0.2), now))
            {
                _controller.Behavior.TriggerEvent(1, BuddyAnimationState.Shrug, BuddyMood.Calm, BuddyAccessory.None, 2.0);
            }
            return;
        }

        if (!CheckCooldown("bt_connected", TimeSpan.FromSeconds(0.2), now)) return;

        bool isAudio = text.Contains("headphone") || text.Contains("headset") || text.Contains("earbud") || text.Contains("airpod");
        bool isLowBattery = text.Contains("low") || text.Contains("10%") || text.Contains("15%");

        if (isAudio)
        {
            _controller.Behavior.TriggerEvent(2, BuddyAnimationState.HeadBobbing, BuddyMood.Happy, BuddyAccessory.Headphones, 3.0);
        }
        else if (isLowBattery)
        {
            _controller.Behavior.TriggerEvent(2, BuddyAnimationState.Surprise, BuddyMood.Sweat, BuddyAccessory.BatteryAlert, 3.0);
        }
        else
        {
            _controller.Behavior.TriggerEvent(2, BuddyAnimationState.Waving, BuddyMood.Happy, BuddyAccessory.None, 2.5);
        }
    }

    private void HandleBattery(BarEvent evt, DateTime now)
    {
        bool charging = evt.Subtitle?.Contains("plugged", StringComparison.OrdinalIgnoreCase) == true
            || evt.Title?.Contains("charging", StringComparison.OrdinalIgnoreCase) == true;

        double pct = evt.Value.HasValue ? evt.Value.Value * 100.0 : -1;
        if (pct < 0 && double.TryParse(evt.ValueText?.Replace("%", "").Trim(), out double parsed))
            pct = parsed;

        if (charging)
        {
            _batteryLowAlertFired = false;
            if (CheckCooldown("battery_charging", TimeSpan.FromSeconds(0.2), now))
            {
                if (pct >= 99.0)
                {
                    _controller.Behavior.TriggerEvent(2, BuddyAnimationState.PatBelly, BuddyMood.Happy, BuddyAccessory.None, 3.5);
                }
                else
                {
                    _controller.Behavior.TriggerEvent(2, BuddyAnimationState.Munch, BuddyMood.Happy, BuddyAccessory.CableSparks, 3.5);
                }
            }
        }
        else
        {
            if (pct > 0 && pct <= 10.0)
            {
                _controller.Behavior.TriggerEvent(3, BuddyAnimationState.LieDownNap, BuddyMood.Sleepy, BuddyAccessory.Zzz, 30.0);
            }
            else if (pct > 0 && pct <= 15.0 && !_batteryLowAlertFired)
            {
                _batteryLowAlertFired = true;
                _controller.Behavior.TriggerEvent(3, BuddyAnimationState.Yawn, BuddyMood.Hungry, BuddyAccessory.Exclamation, 4.0);
            }
        }
    }

    private void HandleAudioOutput(BarEvent evt, DateTime now)
    {
        if (!CheckCooldown("audio_output", TimeSpan.FromSeconds(0.2), now)) return;

        string text = (evt.Title + " " + (evt.Subtitle ?? "")).ToLowerInvariant();
        bool isHeadphones = text.Contains("headphone") || text.Contains("headset") || text.Contains("earbud");

        if (isHeadphones)
        {
            _controller.Behavior.TriggerEvent(2, BuddyAnimationState.HeadBobbing, BuddyMood.Happy, BuddyAccessory.Headphones, 3.0);
        }
        else
        {
            _controller.Behavior.TriggerEvent(2, BuddyAnimationState.Waving, BuddyMood.Happy, BuddyAccessory.Speaker, 3.0);
        }
    }

    private void HandlePrivacy(BarEvent evt, DateTime now)
    {
        string text = (evt.Title + " " + (evt.Subtitle ?? "")).ToLowerInvariant();
        bool mic = text.Contains("mic");
        bool cam = text.Contains("cam");

        if (mic)
        {
            _micInUse = true;
            _callActive = true;
            _controller.Behavior.TriggerEvent(3, BuddyAnimationState.Shh, BuddyMood.Calm, BuddyAccessory.None, 4.0);
        }
        else if (cam)
        {
            _cameraInUse = true;
            _controller.Behavior.TriggerEvent(3, BuddyAnimationState.StraightenOutfit, BuddyMood.Calm, BuddyAccessory.None, 3.5);
        }
    }

    public void OnPrivacyReleased()
    {
        _micInUse = false;
        _cameraInUse = false;
        _callActive = false;
        _controller.Behavior.RestorePreviousSession();
    }

    private void HandleClipboard(BarEvent evt, DateTime now)
    {
        if (!CheckCooldown("clipboard", TimeSpan.FromSeconds(0.2), now)) return;

        string text = (evt.Title + " " + (evt.Subtitle ?? "")).ToLowerInvariant();
        if (text.Contains("image") || text.Contains("picture"))
        {
            _controller.Behavior.TriggerEvent(1, BuddyAnimationState.HeadBobbing, BuddyMood.Happy, BuddyAccessory.PictureFrame, 2.0);
        }
        else if (text.Contains("file"))
        {
            _controller.Behavior.TriggerEvent(1, BuddyAnimationState.HeadBobbing, BuddyMood.Happy, BuddyAccessory.Box, 2.0);
        }
        else
        {
            _controller.Behavior.TriggerEvent(1, BuddyAnimationState.HeadBobbing, BuddyMood.Happy, BuddyAccessory.Clipboard, 2.0);
        }
    }

    public void OnCapsLockChanged(bool isOn, DateTime now)
    {
        var s = _getSettings();
        if (!s.FeatureBuddy || !s.BuddyReactionsEnabled || !s.BuddyReactCapsLock) return;

        if (isOn)
        {
            // Turn ON: puff up, wide mouth, "A" letter pops out!
            _controller.Behavior.TriggerEvent(1, BuddyAnimationState.PuffUp, BuddyMood.Excited, BuddyAccessory.PopoutA, 3.0);
        }
        else
        {
            // Turn OFF: INSTANT SHUTOFF! ZERO COOLDOWN! ZERO DELAY!
            _controller.Behavior.ClearActiveEvent();
            _controller.Behavior.RestorePreviousSession();
        }
    }

    public void OnDndChanged(bool isDnd, DateTime now)
    {
        _isDnd = isDnd;
        var s = _getSettings();
        if (!s.FeatureBuddy || !s.BuddyReactionsEnabled || !s.BuddyReactDnd) return;
        if (!CheckCooldown("dnd", TimeSpan.FromSeconds(0.2), now)) return;

        if (isDnd)
        {
            _controller.Behavior.TriggerEvent(2, BuddyAnimationState.Sleeping, BuddyMood.Sleepy, BuddyAccessory.Moon, 5.0);
        }
        else
        {
            _controller.Behavior.TriggerEvent(2, BuddyAnimationState.Stretching, BuddyMood.Happy, BuddyAccessory.None, 3.0);
        }
    }

    private void QueueSpokenLine(string line, string? capsuleTitle, DateTime now)
    {
        if (string.Equals(line, capsuleTitle, StringComparison.OrdinalIgnoreCase)) return;
        if (now - _lastStatusSpokenTime < TimeSpan.FromSeconds(0.2)) return;

        _pendingStatusLine = line;
        _lastSpokenCapsuleText = capsuleTitle;
    }

    private void OnBusIdle()
    {
        if (_pendingStatusLine is not null)
        {
            string line = _pendingStatusLine;
            _pendingStatusLine = null;
            _lastStatusSpokenTime = DateTime.UtcNow;

            _controller.Say(line, new BuddyController.SpeechOptions
            {
                Priority = 1,
                Mood = BuddyMood.Happy
            });
        }

        if (_controller.Behavior.CurrentAnimation == BuddyAnimationState.Peek)
        {
            _controller.Behavior.RestorePreviousSession();
        }
    }

    private static uint GetEventColor(BarEventKind kind) => kind switch
    {
        BarEventKind.Battery => 0xFF30D158,
        BarEventKind.Network => 0xFF0A84FF,
        BarEventKind.Bluetooth => 0xFF5E5CE6,
        BarEventKind.Privacy => 0xFFFF9F0A,
        BarEventKind.Screenshot => 0xFFFFD60A,
        BarEventKind.Volume => 0xFF64D2FF,
        _ => 0xFF30D158
    };

    public void Dispose()
    {
        _bus.EventRaised -= OnBusEventRaised;
        _bus.Idle -= OnBusIdle;
        _wifiDisconnectDebounce?.Stop();
    }
}
