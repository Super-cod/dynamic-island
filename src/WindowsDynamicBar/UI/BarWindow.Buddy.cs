using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Buddy;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Media;
using WindowsDynamicBar.Services.SystemMonitor;
using WindowsDynamicBar.Services.Weather;
using WindowsDynamicBar.Services.Calendar;

namespace WindowsDynamicBar.UI;

public partial class BarWindow
{
    private Buddy.BuddyController? _buddyController;
    private bool _buddyTwoLines;

    // Real context state trackers
    private bool _lastInternetConnected = true;
    private readonly DispatcherTimer _netDebounceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DateTime _lastWeatherCommentTime = DateTime.MinValue;
    private string _lastWeatherCondition = "";
    private DateTime _lastMusicCommentTime = DateTime.MinValue;
    private string _lastMusicTrackKey = "";
    private bool _lastBatteryWasLow;
    private bool _lastBatteryWasOnAc;
    private readonly DispatcherTimer _ramHighTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _ramHighTimerActive;

    private bool _isBuddyCardOpen;
    public bool IsBuddyCardOpen => _isBuddyCardOpen;

    public bool IsInternetConnected => _lastInternetConnected;
    public bool IsBatteryCharging => _hasBattery && _battOnAc;
    public bool IsBatteryLow => _hasBattery && _battPct >= 0 && _battPct <= 15 && !_battOnAc;
    public bool IsRamHigh => _ramHighTimerActive;
    public bool IsMusicActive => IsMusicPlaying();

    public bool IsOverlayOrCardOpen =>
        _isBuddyCardOpen
        || _islandState is IslandVisualState.Search or IslandVisualState.Detail
        || _expanded
        || _activeMetric != IslandMetric.None
        || (VolumeHudGlow?.Visibility == Visibility.Visible);

    public bool IsDndOrFullscreenOrFocus
    {
        get
        {
            if (App.GamingMode || (_cfg.AutoHideFullscreen && NativeMethods.IsWindowFullscreen(NativeMethods.GetForegroundWindow())))
                return true;
            if (App.Focus?.Current is { Running: true })
                return true;
            // The Focus button's session is the "Focus" timer (see SyncBuddyFocusSession).
            if (_focusTimerActive && _lastTimer?.State == Services.Timers.TimerState.Running)
                return true;
            if (_cfg.DoNotDisturb)
                return true;
            return false;
        }
    }

    private void InitializeBuddy()
    {
        Log.Info("InitializeBuddy called", ("hasSprite", BuddySprite is not null), ("hasContainer", BuddyContainer is not null));
        if (BuddySprite is null || BuddyContainer is null || BuddySpeechText is null || BuddyClockRow is null)
        {
            Log.Warning("InitializeBuddy aborted: controls missing");
            return;
        }

        _buddyController = new Buddy.BuddyController(
            this,
            BuddySprite,
            BuddyContainer,
            BuddyTransform,
            BuddySpeechText,
            BuddySpeechText,
            BuddyClockRow,
            BuddyCardComponent
        );
        CheckDailyGreetingAndStreak();
        StartReminders();
        SyncBuddyLayoutMode();
        if (_lastMedia is not null)
        {
            OnBuddyMediaUpdated(_lastMedia);
        }
        else if (App.Media?.Current is not null)
        {
            OnBuddyMediaUpdated(App.Media.Current);
        }
        Log.Info("BuddyController successfully created and attached");

        // Network monitoring with 3-second debounce
        _lastInternetConnected = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _netDebounceTimer.Tick += OnNetDebounceTick;

        // RAM sustained monitor
        _ramHighTimer.Tick += OnRamHighTimerTick;

        // Battery: start from the current state so launching on AC (or already low) says nothing.
        _lastBatteryWasOnAc = _battOnAc;
        _lastBatteryWasLow = IsBatteryLow;
        _lastBatteryWasFull = _hasBattery && _battPct >= 99 && _battOnAc;

        // Hook Window visibility & layout changes
        IsVisibleChanged += (_, _) => _buddyController?.OnIslandVisibilityChanged(IsVisible);
    }

    private void TeardownBuddy()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _netDebounceTimer.Stop();
        _ramHighTimer.Stop();
        _waterReminderTimer?.Stop();
        _eyeReminderTimer?.Stop();

        _buddyController?.Dispose();
        _buddyController = null;
    }

    // ---------------------------------------------------------------- Buddy Card & Daily Streak
    private DispatcherTimer? _waterReminderTimer;
    private DispatcherTimer? _eyeReminderTimer;

    private void OnBuddyCardButtonClick(object sender, RoutedEventArgs e)
    {
        _buddyController?.ToggleCard();
    }

    public void OpenBuddyCard()
    {
        if (_isBuddyCardOpen) return;
        _isBuddyCardOpen = true;

        if (BuddyCardComponent is not null)
        {
            BuddyCardComponent.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
            BuddyCardComponent.BeginAnimation(OpacityProperty, fadeIn);
            _buddyController?.RefreshCardIfOpen();
        }

        CollapsedPanel.Visibility = Visibility.Collapsed;
        ExpandedHost.Visibility = Visibility.Collapsed;
        IdleOpenPanel.Visibility = Visibility.Collapsed;
        if (BuddyContainer is not null) BuddyContainer.Visibility = Visibility.Collapsed;

        // Sized to the content: a fixed 460 x 180 cut off the stats line (bottom) and the third
        // meter (right). The meter row needs ~486 px; the outfit list wraps to the width.
        double targetW = Math.Min(500, _winW - 40);
        double targetH = 180;
        if (BuddyCardComponent is not null)
        {
            // Inner width = island minus its 1 px border each side; a little slack so the estimate can
            // only wrap the outfit list onto more rows (taller), never fewer (2 px decided a row).
            BuddyCardComponent.Measure(new Size(targetW - 2 - 4, double.PositiveInfinity));
            targetH = Math.Clamp(Math.Ceiling(BuddyCardComponent.DesiredSize.Height) + 2, 180, MaxPillH);
        }
        double targetR = 34;

        _anim.Animate("w", _currentW, targetW, ApplyW);
        _anim.Animate("h", _currentH, targetH, ApplyH);
        _anim.Animate("radius", _currentCornerRadius, targetR, ApplyCornerRadius);
    }

    public void CloseBuddyCard()
    {
        if (!_isBuddyCardOpen) return;
        _isBuddyCardOpen = false;

        if (BuddyCardComponent is not null)
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (_, _) => BuddyCardComponent.Visibility = Visibility.Collapsed;
            BuddyCardComponent.BeginAnimation(OpacityProperty, fadeOut);
        }

        if (BuddyContainer is not null)
        {
            BuddyContainer.Visibility = App.Settings?.Current.FeatureBuddy == true ? Visibility.Visible : Visibility.Collapsed;
        }

        ApplyLayoutMode();
        SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);
        SyncBuddyLayoutMode();
    }

    public void SyncBuddyLayoutMode()
    {
        if (_buddyController is null) return;
        IslandMode mode = IslandMode.Collapsed;
        if (_isBuddyCardOpen)
        {
            return;
        }
        else if (_islandState == IslandVisualState.Detail)
        {
            mode = IslandMode.Detail;
        }
        else if (_expanded || _islandState == IslandVisualState.Expanded)
        {
            mode = _idleMode ? IslandMode.ExpandedDashboard : IslandMode.ExpandedMusic;
        }

        _buddyController.UpdateLayout(mode, _currentW, _currentH);
    }

    private void CheckDailyGreetingAndStreak()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy) return;

        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (s.BuddyLastUsedDate != today)
        {
            string yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
            int streak = (s.BuddyLastUsedDate == yesterday) ? s.BuddyStreak + 1 : 1;

            App.Settings?.Update(cfg =>
            {
                cfg.BuddyLastUsedDate = today;
                cfg.BuddyStreak = streak;
            });

            if (s.BuddyDailyGreeting)
            {
                string line = string.Format(BuddyLines.DailyGreeting, streak);
                _buddyController?.Say(line, new Buddy.BuddyController.SpeechOptions
                {
                    Priority = 3,
                    Mood = BuddyMood.Excited
                });
            }
        }
    }

    private void StartReminders()
    {
        _waterReminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(45) };
        _waterReminderTimer.Tick += (_, _) =>
        {
            var s = App.Settings?.Current;
            if (s is null || !s.FeatureBuddy || !s.BuddyRemindWater) return;
            if (IsDndOrFullscreenOrFocus) return;

            _buddyController?.Say(BuddyLines.WaterBreak, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 2,
                Mood = BuddyMood.Happy,
                Accessory = BuddyAccessory.Drop
            });
        };
        _waterReminderTimer.Start();

        _eyeReminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        _eyeReminderTimer.Tick += (_, _) =>
        {
            var s = App.Settings?.Current;
            if (s is null || !s.FeatureBuddy || !s.BuddyRemindEyes) return;
            if (IsDndOrFullscreenOrFocus) return;

            _buddyController?.Say(BuddyLines.EyeBreak, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 2,
                Mood = BuddyMood.Happy
            });
        };
        _eyeReminderTimer.Start();
    }

    // ---------------------------------------------------------------- Two-line pill spring growth

    public void SetBuddyTwoLines(bool twoLines)
    {
        if (_buddyTwoLines == twoLines) return;
        _buddyTwoLines = twoLines;

        if (_islandState == IslandVisualState.Collapsed && !_expanded)
        {
            double targetH = twoLines ? 68.0 : CollapsedH;
            double targetR = twoLines ? 30.0 : CollapsedRadius;

            _anim.Animate("h", _currentH, targetH, ApplyH);
            _anim.Animate("radius", _currentCornerRadius, targetR, ApplyCornerRadius);
        }
    }

    // ---------------------------------------------------------------- Mouse & Eye interaction

    private void HandleBuddyPointerEnter()
    {
        _buddyController?.OnPointerEnter();
    }

    private void HandleBuddyPointerMove(MouseEventArgs e)
    {
        if (Pill is not null)
        {
            Point pt = e.GetPosition(Pill);
            _buddyController?.OnPointerMove(pt, new Size(Pill.ActualWidth, Pill.ActualHeight));
        }
    }

    private void HandleBuddyPointerLeave()
    {
        _buddyController?.OnPointerLeave();
    }

    // ---------------------------------------------------------------- Real Context: Weather

    public void OnBuddyWeatherUpdated(WeatherSnapshot? snap)
    {
        if (snap is null || !snap.Available) return;
        var settings = App.Settings?.Current;
        if (settings is null || !settings.FeatureBuddy || !settings.BuddyWeatherComments) return;

        int hour = DateTime.Now.Hour;
        bool isSunnyMidday = snap.Kind == WeatherKind.Sun && hour >= 10 && hour < 16;
        bool isRain = snap.Kind is WeatherKind.Rain or WeatherKind.Storm || snap.CurrentPrecipProb > 50;
        bool isHot = snap.TempC >= 34;
        bool isCold = snap.TempC <= 12;

        // Music owns the accessory (headphones) while it plays; weather only sets the mood then.
        var keep = IsMusicPlaying() ? BuddyAccessory.Headphones : (BuddyAccessory?)null;
        if (isRain)
            _buddyController?.SetBaseline(BuddyMood.Happy, keep ?? BuddyAccessory.Umbrella);
        else if (isSunnyMidday)
            _buddyController?.SetBaseline(BuddyMood.Cool, keep ?? BuddyAccessory.None);
        else if (isHot)
            _buddyController?.SetBaseline(BuddyMood.Sweat, keep ?? BuddyAccessory.None);
        else if (isCold)
            _buddyController?.SetBaseline(BuddyMood.Cold, keep ?? BuddyAccessory.None);
        else
            _buddyController?.SetBaseline(BuddyMood.Happy, keep ?? BuddyAccessory.None);

        string currentCond = snap.Condition;
        if (currentCond != _lastWeatherCondition && DateTime.Now - _lastWeatherCommentTime >= TimeSpan.FromHours(3))
        {
            _lastWeatherCondition = currentCond;
            _lastWeatherCommentTime = DateTime.Now;

            string? line = null;
            BuddyMood mood = BuddyMood.Happy;
            BuddyAccessory acc = BuddyAccessory.None;

            if (isRain)
            {
                line = BuddyLines.WeatherRain;
                acc = BuddyAccessory.Umbrella;
            }
            else if (isHot)
            {
                line = BuddyLines.WeatherHot;
                mood = BuddyMood.Sweat;
            }
            else if (isCold)
            {
                line = BuddyLines.WeatherCold;
                mood = BuddyMood.Cold;
            }
            else if (isSunnyMidday)
            {
                line = BuddyLines.WeatherSunny;
                mood = BuddyMood.Cool;
            }

            if (line is not null)
            {
                _buddyController?.Say(line, new Buddy.BuddyController.SpeechOptions
                {
                    Priority = 2,
                    Mood = mood,
                    Accessory = acc
                });
            }
        }
    }

    // ---------------------------------------------------------------- Real Context: Internet

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Dispatcher.BeginInvoke(RestartNetDebounce);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(RestartNetDebounce);
    }

    private void RestartNetDebounce()
    {
        _netDebounceTimer.Stop();
        _netDebounceTimer.Start();
    }

    private void OnNetDebounceTick(object? sender, EventArgs e)
    {
        _netDebounceTimer.Stop();
        bool isConnected = NetworkInterface.GetIsNetworkAvailable();
        if (isConnected != _lastInternetConnected)
        {
            _lastInternetConnected = isConnected;
            var settings = App.Settings?.Current;
            if (settings is null || !settings.FeatureBuddy || !settings.BuddySystemComments) return;

            if (isConnected)
            {
                _buddyController?.SetBaseline(BuddyMood.Happy);
                _buddyController?.Say(BuddyLines.NetOnline, new Buddy.BuddyController.SpeechOptions
                {
                    Priority = 3,
                    Mood = BuddyMood.Excited
                });
            }
            else
            {
                _buddyController?.SetBaseline(BuddyMood.Sad);
                _buddyController?.Say(BuddyLines.NetOffline, new Buddy.BuddyController.SpeechOptions
                {
                    Priority = 3,
                    Mood = BuddyMood.Sad
                });
            }
        }
    }

    // ---------------------------------------------------------------- Real Context: Battery

    public void OnBuddyBatteryUpdated(bool hasBattery, double pct, bool onAc)
    {
        if (!hasBattery || pct < 0) return;
        var settings = App.Settings?.Current;
        if (settings is null || !settings.FeatureBuddy || !settings.BuddySystemComments) return;

        bool isLow = pct <= 15 && !onAc;
        bool isFull = pct >= 99 && onAc;

        if (isLow && !_lastBatteryWasLow)
        {
            _lastBatteryWasLow = true;
            _buddyController?.SetBaseline(BuddyMood.Hungry);
            _buddyController?.Say(BuddyLines.BatteryLow, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 3,
                Mood = BuddyMood.Hungry
            });
        }
        else if (!isLow)
        {
            _lastBatteryWasLow = false;
        }

        if (onAc && !_lastBatteryWasOnAc)
        {
            _lastBatteryWasOnAc = true;
            _buddyController?.SetBaseline(BuddyMood.Happy);
            _buddyController?.Say(BuddyLines.BatteryCharging, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 3,
                Mood = BuddyMood.Excited
            });
        }
        else if (!onAc)
        {
            _lastBatteryWasOnAc = false;
        }
        else if (isFull && !_lastBatteryWasFull)
        {
            _buddyController?.Say(BuddyLines.BatteryFull, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 3,
                Mood = BuddyMood.Happy
            });
        }
        // Once per charge: power events keep arriving while it sits at 100 %.
        _lastBatteryWasFull = isFull;
    }

    private bool _lastBatteryWasFull;

    // ---------------------------------------------------------------- Real Context: RAM

    public void OnBuddyStatsUpdated(SystemStats? stats)
    {
        if (stats is null) return;
        var settings = App.Settings?.Current;
        if (settings is null || !settings.FeatureBuddy || !settings.BuddySystemComments) return;

        if (stats.Ram >= 90.0)
        {
            if (!_ramHighTimerActive)
            {
                _ramHighTimerActive = true;
                _ramHighTimer.Start();
            }
        }
        else
        {
            _ramHighTimerActive = false;
            _ramHighTimer.Stop();
        }
    }

    private void OnRamHighTimerTick(object? sender, EventArgs e)
    {
        _ramHighTimer.Stop();
        _ramHighTimerActive = false;

        string line = BuddyLines.RamHigh;
        var topProc = GetTopMemoryProcess();
        if (topProc is not null)
        {
            line = $"RAM is high ({topProc.Value.Item1} using {topProc.Value.Item2}).";
        }

        _buddyController?.SetOverride(BuddyMood.Sweat, null, 4000);
        _buddyController?.Say(line, new Buddy.BuddyController.SpeechOptions
        {
            Priority = 3,
            Mood = BuddyMood.Sweat
        });
    }

    // ---------------------------------------------------------------- Real Context: Music

    public void OnBuddyMediaUpdated(MediaInfo? media)
    {
        var settings = App.Settings?.Current;
        if (settings is null || !settings.FeatureBuddy) return;

        _buddyController?.OnMediaPlaybackStateChanged(media);

        bool isPlaying = media is { IsPlaying: true };
        if (isPlaying)
        {
            _buddyController?.SetBaseline(BuddyMood.Happy, BuddyAccessory.Headphones);

            string key = $"{media!.Artist}|{media.Title}";
            if (key != _lastMusicTrackKey && DateTime.Now - _lastMusicCommentTime >= TimeSpan.FromMinutes(10))
            {
                _lastMusicTrackKey = key;
                _lastMusicCommentTime = DateTime.Now;

                _buddyController?.Say(BuddyLines.MusicStart, new Buddy.BuddyController.SpeechOptions
                {
                    Priority = 2,
                    Mood = BuddyMood.Happy,
                    Accessory = BuddyAccessory.Headphones
                });
            }
        }
        else if (media is null || (string.IsNullOrWhiteSpace(media.Title) && string.IsNullOrWhiteSpace(media.AppId)))
        {
            _buddyController?.SetBaseline(BuddyMood.Happy, BuddyAccessory.None);
        }
    }

    // ---------------------------------------------------------------- Feature Moments
    private DateTime _lastNotifTime = DateTime.MinValue;
    private bool _hasSaidMirrorToday;
    private DateTime _lastSearchOpenedTime = DateTime.MinValue;
    private DateTime _lastCalendarCheckTime = DateTime.MinValue;
    private string _lastCalendarEventKey = "";

    public void OnBuddyFocusStarted()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentFocus) return;

        if (_buddyController is not null) _buddyController.Behavior.IsFocusActive = true;
        _buddyController?.Say(BuddyLines.FocusStart, new Buddy.BuddyController.SpeechOptions
        {
            Priority = 3,
            Mood = BuddyMood.Happy,
            Accessory = BuddyAccessory.Hourglass
        });
    }

    public void OnBuddyFocusFinished()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentFocus) return;

        if (_buddyController is not null) _buddyController.Behavior.IsFocusActive = false;
        App.Settings?.Update(cfg => cfg.BuddyFocusSessions++);

        _buddyController?.Say(BuddyLines.FocusEnd, new Buddy.BuddyController.SpeechOptions
        {
            Priority = 3,
            Mood = BuddyMood.Excited,
            Animation = BuddyAnimationState.Celebrating
        });
    }

    public void OnBuddyFocusCancelled()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy) return;
        if (_buddyController is not null) _buddyController.Behavior.IsFocusActive = false;
        _buddyController?.SetBaseline(BuddyMood.Happy, BuddyAccessory.None);
    }

    public void OnBuddyScreenshotTaken()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentScreenshot) return;
        _buddyController?.Say(BuddyLines.Screenshot, new Buddy.BuddyController.SpeechOptions
        {
            Priority = 2,
            Mood = BuddyMood.Happy,
            Accessory = BuddyAccessory.Camera
        });
    }

    public void OnBuddyCopied()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentClipboard) return;
        _buddyController?.Say(BuddyLines.Copied, new Buddy.BuddyController.SpeechOptions
        {
            Priority = 2,
            Mood = BuddyMood.Happy,
            Accessory = BuddyAccessory.Clip
        });
    }

    public void OnBuddyNotificationShown(bool isCall)
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentNotif) return;

        if (DateTime.Now - _lastNotifTime < TimeSpan.FromSeconds(2.5)) return;
        _lastNotifTime = DateTime.Now;

        if (isCall)
        {
            _buddyController?.Say(BuddyLines.IncomingCall, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 3,
                Mood = BuddyMood.Surprised,
                Accessory = BuddyAccessory.Phone
            });
        }
        else
        {
            _buddyController?.Say(BuddyLines.NotifDelivery, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 3,
                Mood = BuddyMood.Excited,
                Accessory = BuddyAccessory.Envelope
            });
        }
    }

    public void OnBuddySpeedTestFinished(double mbps)
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentSpeed) return;

        if (mbps >= 100.0)
        {
            string line = string.Format(BuddyLines.SpeedFast, Math.Round(mbps));
            _buddyController?.Say(line, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 2,
                Mood = BuddyMood.Excited,
                Accessory = BuddyAccessory.Run
            });
        }
        else if (mbps < 10.0)
        {
            string line = string.Format(BuddyLines.SpeedSlow, Math.Round(mbps));
            _buddyController?.Say(line, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 2,
                Mood = BuddyMood.Sad,
                Accessory = BuddyAccessory.Snail
            });
        }
        else
        {
            string line = string.Format(BuddyLines.SpeedNormal, Math.Round(mbps));
            _buddyController?.Say(line, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 2,
                Mood = BuddyMood.Happy
            });
        }
    }

    public void OnBuddyMirrorOpened()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentMirror) return;

        if (!_hasSaidMirrorToday)
        {
            _hasSaidMirrorToday = true;
            _buddyController?.Say(BuddyLines.MirrorFirstTime, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 2,
                Mood = BuddyMood.Cool
            });
        }
    }

    public void OnBuddySearchOpened()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentSearch) return;

        if (DateTime.Now - _lastSearchOpenedTime >= TimeSpan.FromHours(1))
        {
            _lastSearchOpenedTime = DateTime.Now;
            _buddyController?.Say(BuddyLines.SearchOpened, new Buddy.BuddyController.SpeechOptions
            {
                Priority = 2,
                Mood = BuddyMood.Happy,
                Accessory = BuddyAccessory.Glass
            });
        }
    }

    public void OnBuddySearchFound()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentSearch) return;

        _buddyController?.Say(BuddyLines.SearchFound, new Buddy.BuddyController.SpeechOptions
        {
            Priority = 2,
            Mood = BuddyMood.Happy
        });
    }

    public void OnBuddySearchClosed()
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy) return;
        if (_buddyController is not null && _buddyController.Behavior.CurrentAccessory == BuddyAccessory.Glass)
        {
            _buddyController.SetBaseline(_buddyController.Behavior.BaselineMood, _buddyController.Behavior.BaselineAccessory);
        }
    }

    public void OnBuddyCalendarCheck(CalendarService calendar, DateTime now)
    {
        var s = App.Settings?.Current;
        if (s is null || !s.FeatureBuddy || !s.BuddyCommentCalendar) return;

        var ev = calendar.NextAfter(now);
        if (ev is null) return;

        TimeSpan diff = ev.Start - now;
        if (diff >= TimeSpan.FromMinutes(9) && diff <= TimeSpan.FromMinutes(11))
        {
            string key = $"{ev.Title}|{ev.Start:HHmm}";
            if (key != _lastCalendarEventKey)
            {
                _lastCalendarEventKey = key;
                string line = calendar.HasCalendar
                    ? string.Format(BuddyLines.CalendarUpcoming, ev.Title)
                    : BuddyLines.CalendarGeneric;

                _buddyController?.Say(line, new Buddy.BuddyController.SpeechOptions
                {
                    Priority = 3,
                    Mood = BuddyMood.Surprised,
                    Accessory = BuddyAccessory.Bell
                });
            }
        }
    }

    // ---------------------------------------------------------------- Debug / Trigger helpers

    public void TriggerBuddySpeech(string text, int priority = 1, BuddyMood? mood = null, BuddyAccessory? acc = null)
    {
        _buddyController?.Say(text, new Buddy.BuddyController.SpeechOptions
        {
            Priority = priority,
            Mood = mood,
            Accessory = acc
        });
    }

    public void TriggerBuddyPet()
    {
        _buddyController?.Say(BuddyLines.PickNonRepeating(BuddyLines.Pet), new Buddy.BuddyController.SpeechOptions
        {
            Priority = 2,
            Mood = BuddyMood.Love
        });
    }

    public void TriggerBuddyJoke()
    {
        _buddyController?.Say(BuddyLines.PickNonRepeating(BuddyLines.Jokes), new Buddy.BuddyController.SpeechOptions
        {
            Priority = 2,
            Mood = BuddyMood.Excited
        });
    }

#if DEBUG
    partial void RunDebugCommandExtra(string[] args)
    {
        if (args.Length == 0) return;
        switch (args[0])
        {
            case "buddy-anim" when args.Length > 1:
                if (Enum.TryParse<BuddyAnimationState>(args[1], true, out var anim))
                    _buddyController?.Behavior.TriggerEvent(2, anim, BuddyMood.Happy, BuddyAccessory.None, 3.0);
                break;
            case "buddy-mood" when args.Length > 1:
                if (Enum.TryParse<BuddyMood>(args[1], true, out var mood))
                    _buddyController?.SetOverride(mood, null, 3000);
                break;
            case "buddy-acc" when args.Length > 1:
                if (Enum.TryParse<BuddyAccessory>(args[1], true, out var acc))
                    _buddyController?.SetOverride(BuddyMood.Happy, acc, 3000);
                break;
            case "buddy-outfit" when args.Length > 1:
                if (Enum.TryParse<BuddyOutfit>(args[1], true, out var outfit))
                    _buddyController?.SetOutfit(outfit);
                break;
            case "buddy-walk" when args.Length > 1:
                if (double.TryParse(args[1], out double wx))
                    _buddyController?.Behavior.Movement.MoveTo(wx);
                break;
            case "buddy-peek":
                _buddyController?.Behavior.TriggerEvent(2, BuddyAnimationState.EdgePeeking, BuddyMood.Curious, BuddyAccessory.None, 3.0);
                break;
            case "buddy-card":
                _buddyController?.ToggleCard();
                break;
            case "buddy-say" when args.Length > 1:
                _buddyController?.Say(string.Join(' ', System.Linq.Enumerable.Skip(args, 1)), new Buddy.BuddyController.SpeechOptions { Priority = 2 });
                break;
            case "buddy-pet":
                TriggerBuddyPet();
                break;
            case "buddy-joke":
                TriggerBuddyJoke();
                break;
            case "buddy-reset":
                App.Settings?.Update(cfg =>
                {
                    cfg.BuddyName = "Dot";
                    cfg.BuddyPersonality = BuddyPersonality.Playful;
                    cfg.BuddyOutfit = BuddyOutfit.None;
                    cfg.BuddyPets = 0;
                    cfg.BuddyStreak = 1;
                    cfg.BuddyLastUsedDate = "";
                    cfg.BuddyFocusSessions = 0;
                    cfg.BuddyOwnedPacks = new System.Collections.Generic.List<string>();
                });
                break;
        }
    }
#endif
}
