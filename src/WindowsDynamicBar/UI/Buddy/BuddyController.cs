using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Buddy;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.UI.Buddy;

public sealed class BuddyController : IDisposable
{
    private readonly BarWindow _window;
    private readonly BuddySpriteControl _sprite;
    private readonly FrameworkElement _buddyContainer;
    private readonly TranslateTransform? _buddyTransform;
    private readonly TextBlock _speechText;
    private readonly FrameworkElement _speechContainer;
    private readonly FrameworkElement _clockContainer;
    private readonly BuddyCardControl? _cardControl;

    // Behavior engine
    private readonly BuddyBehaviorController _behavior;

    // Timers
    private readonly DispatcherTimer _lifeTimer;
    private readonly DispatcherTimer _typewriterTimer;
    private readonly DispatcherTimer _speechHoldTimer;
    private readonly DispatcherTimer _talkFrequencyTimer;
    private readonly DispatcherTimer _clickTimer;

    // Speech Queue & State
    private readonly Queue<(string Text, SpeechOptions Options)> _speechQueue = new();
    private SpeechOptions? _currentSpeech;
    private string _fullSpeechText = "";
    private int _typewriterCharIndex;

    // Life loop tracking
    private double _hop;
    private double _wave;
    private Point _look = new(0, 0);
    private Point _targetLook = new(0, 0);
    private double _blinkUntilMs;
    private double _lastWanderTimeMs;
    private readonly Stopwatch _lifeClock = Stopwatch.StartNew();
    private readonly List<BuddySpriteControl.Particle> _particles = new();

    // Interaction tracking
    private int _clickCount;
    private bool _pointerOverIsland;
    private bool _isCardOpen;
    private bool _isDisposed;

    public bool IsCardOpen => _isCardOpen;
    public BuddyBehaviorController Behavior => _behavior;

    public sealed class SpeechOptions
    {
        public int Priority { get; set; } = 1;
        public BuddyMood? Mood { get; set; }
        public BuddyAccessory? Accessory { get; set; }
        public BuddyAnimationState? Animation { get; set; }
        public Action? OnComplete { get; set; }
    }

    public BuddyController(
        BarWindow window,
        BuddySpriteControl sprite,
        FrameworkElement buddyContainer,
        TranslateTransform? buddyTransform,
        TextBlock speechText,
        FrameworkElement speechContainer,
        FrameworkElement clockContainer,
        BuddyCardControl? cardControl = null)
    {
        _window = window;
        _sprite = sprite;
        _buddyContainer = buddyContainer;
        _buddyTransform = buddyTransform;
        _speechText = speechText;
        _speechContainer = speechContainer;
        _clockContainer = clockContainer;
        _cardControl = cardControl;

        _behavior = new BuddyBehaviorController();

        if (_cardControl is not null)
        {
            _cardControl.CloseRequested += CloseCard;
            _cardControl.OutfitSelected += SetOutfit;
            _cardControl.PersonalityChanged += p =>
            {
                _behavior.SetPersonality(p);
                Say($"I am feeling {p} now!", new SpeechOptions { Priority = 2, Mood = BuddyMood.Happy });
            };
            _cardControl.SpeakRequested += msg => Say(msg, new SpeechOptions { Priority = 2 });
            _cardControl.DemoPackToggled += packId =>
            {
                var s = App.Settings?.Current;
                if (s is null) return;
                var set = new HashSet<string>(s.BuddyOwnedPacks ?? new List<string>());
                string key = packId.ToString();
                if (set.Contains(key)) set.Remove(key);
                else set.Add(key);
                App.Settings?.Update(cfg => cfg.BuddyOwnedPacks = new List<string>(set));
                RefreshCardIfOpen();
            };
        }

        // Life loop timer: 14 fps => ~71ms
        _lifeTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(71)
        };
        _lifeTimer.Tick += OnLifeTick;

        // Typewriter timer: 28 ms per char
        _typewriterTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(28)
        };
        _typewriterTimer.Tick += OnTypewriterTick;

        // Speech hold timer
        _speechHoldTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _speechHoldTimer.Tick += OnSpeechHoldFinished;

        // Random chatter schedule timer
        _talkFrequencyTimer = new DispatcherTimer(DispatcherPriority.Background);
        _talkFrequencyTimer.Tick += OnTalkTimerTick;

        // Click debounce timer (for distinguishing click vs dblclick)
        _clickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _clickTimer.Tick += OnClickDebounce;

        // Wire inputs
        _sprite.Focusable = true;
        _sprite.ToolTip = "Your buddy. Press Enter to pet it.";
        _sprite.MouseLeftButtonDown += OnSpriteMouseDown;
        _sprite.MouseRightButtonUp += OnSpriteRightMouseUp;
        _sprite.KeyDown += OnSpriteKeyDown;

        UpdateSettings(App.Settings?.Current ?? new SettingsModel());
    }

    public void UpdateSettings(SettingsModel settings)
    {
        if (_isDisposed) return;

        bool enabled = settings.FeatureBuddy && !_window.IsSearchOpen;
        _buddyContainer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        _behavior.CurrentOutfit = settings.BuddyOutfit;
        _behavior.Personality = settings.BuddyPersonality;
        _behavior.ReduceMotion = settings.ReduceMotion;
        _behavior.IsQuiet = settings.BuddyQuiet || IsQuietPeriodActive(settings);

        if (!enabled)
        {
            StopLifeLoop();
            _talkFrequencyTimer.Stop();
            EndSpeechImmediately(force: true);
            return;
        }

        RestartTalkScheduler(settings);
        CheckVisibilityAndRun();
    }

    public void UpdateLayout(IslandMode mode, double width, double height)
    {
        _behavior.Movement.UpdateLayout(mode, width, height, _currentSpeech is not null);
    }

    public void OnIslandResized(double width, double height)
    {
        _behavior.Movement.UpdateLayout(_behavior.Movement.CurrentMode, width, height, _currentSpeech is not null);
    }

    private void RestartTalkScheduler(SettingsModel settings)
    {
        _talkFrequencyTimer.Stop();
        if (settings.BuddyQuiet || IsQuietPeriodActive(settings) || !settings.BuddySpeechEnabled) return;

        double minutes = settings.BuddyTalkFrequency switch
        {
            BuddyTalkFrequency.Rare => 25.0 + (new Random().NextDouble() * 20.0), // 25-45 min
            BuddyTalkFrequency.Often => 2.5 + (new Random().NextDouble() * 1.5),  // ~3 min
            _ => 8.0 + (new Random().NextDouble() * 4.0)                          // ~10 min
        };

        _talkFrequencyTimer.Interval = TimeSpan.FromMinutes(minutes);
        _talkFrequencyTimer.Start();
    }

    private static bool IsQuietPeriodActive(SettingsModel settings) =>
        settings.BuddyQuietUntil.HasValue && DateTime.Now < settings.BuddyQuietUntil.Value;

    public void OnIslandVisibilityChanged(bool isVisible)
    {
        if (isVisible) CheckVisibilityAndRun();
        else StopLifeLoop();
    }

    public void CheckVisibilityAndRun()
    {
        var settings = App.Settings?.Current;
        if (settings is null || !settings.FeatureBuddy || _window.IsSearchOpen)
        {
            StopLifeLoop();
            return;
        }

        if (_window.IsVisible && _window.Visibility == Visibility.Visible && !_window.IsDndOrFullscreenOrFocus)
        {
            if (!_lifeTimer.IsEnabled)
            {
                _lifeClock.Restart();
                _lifeTimer.Start();
            }
        }
        else
        {
            StopLifeLoop();
        }
    }

    public void StopLifeLoop()
    {
        if (_lifeTimer.IsEnabled) _lifeTimer.Stop();
    }

    // ---------------------------------------------------------------- Life loop & rendering

    private void OnLifeTick(object? sender, EventArgs e)
    {
        if (_isDisposed || _window.IsSearchOpen)
        {
            StopLifeLoop();
            return;
        }

        double nowMs = _lifeClock.ElapsedMilliseconds;
        // Effective value pushed by UpdateSettings (Settings.Current is a deep clone: too costly at 14 fps).
        bool rm = _behavior.ReduceMotion;
        var rnd = Random.Shared;

        // Step behavior and movement controller (~71ms dt)
        _behavior.Update(0.071, DateTime.UtcNow);

        // Update physical transform within island
        if (_buddyTransform is not null)
        {
            _buddyTransform.X = _behavior.Movement.X;
            _buddyTransform.Y = _behavior.Movement.Y;
        }

        // Hop animation progress
        if (_hop > 0)
        {
            _hop += 0.071 * 2.2;
            if (_hop >= 1.0) _hop = 0;
        }

        // Wave animation progress
        if (_wave > 0)
        {
            _wave -= 0.071;
            if (_wave < 0) _wave = 0;
        }

        // Look gaze damping towards target
        _look = new Point(
            _look.X + (_targetLook.X - _look.X) * 0.25,
            _look.Y + (_targetLook.Y - _look.Y) * 0.25
        );

        // Blinking
        bool isBlinking = !rm && nowMs < _blinkUntilMs;
        if (!rm && nowMs >= _blinkUntilMs + 130 && rnd.NextDouble() < 0.015)
        {
            _blinkUntilMs = nowMs + 130;
        }

        // Idle eye wander when mouse is not over island
        if (!_pointerOverIsland && nowMs - _lastWanderTimeMs > 2500 + (rnd.NextDouble() * 3000))
        {
            _lastWanderTimeMs = nowMs;
            int[] optsX = { -1, 0, 1, 0 };
            int[] optsY = { 0, 0, 1 };
            _targetLook = new Point(optsX[rnd.Next(optsX.Length)], optsY[rnd.Next(optsY.Length)]);
        }

        // Heart particles during Love mood
        if (_behavior.CurrentMood == BuddyMood.Love && rnd.NextDouble() < 0.2)
        {
            _particles.Add(new BuddySpriteControl.Particle
            {
                Kind = 'h',
                X = 12 + rnd.NextDouble() * 2.0,
                Y = 4,
                Vy = -0.25,
                Life = 14
            });
        }

        // Particle updates
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Y += p.Vy;
            p.X += p.Vx;
            p.Life--;
            if (p.Life <= 0) _particles.RemoveAt(i);
        }

        bool isTalking = _currentSpeech is not null;

        _sprite.RenderFrame(
            _behavior.CurrentMood,
            _behavior.CurrentAccessory,
            nowMs,
            rm,
            isTalking,
            _hop,
            _wave,
            _look.X,
            _look.Y,
            isBlinking,
            _behavior.CurrentOutfit,
            _particles,
            _behavior.CurrentAnimation,
            _behavior.Movement.FacingLeft,
            _behavior.Movement.WalkFrame
        );

        if (_isCardOpen && _cardControl is not null)
        {
            _cardControl.UpdateBigSprite(_sprite.Bitmap);
        }
    }

    public void OnPointerEnter()
    {
        if (_window.IsSearchOpen) return;
        _pointerOverIsland = true;
        _wave = 1.2;
    }

    public void OnPointerMove(Point islandRelativePoint, Size islandSize)
    {
        if (_window.IsSearchOpen) return;
        _pointerOverIsland = true;
        double dx = (islandRelativePoint.X - 25.0) / 60.0;
        double dy = (islandRelativePoint.Y - 21.0) / 40.0;
        _targetLook = new Point(Math.Clamp(dx, -1.0, 1.0), Math.Clamp(dy, -1.0, 1.0));
    }

    public void OnPointerLeave()
    {
        if (_window.IsSearchOpen) return;
        _pointerOverIsland = false;
        _targetLook = new Point(0, 0);
    }

    // ---------------------------------------------------------------- Speech System

    public void Say(string text, SpeechOptions? options = null)
    {
        var settings = App.Settings?.Current;
        if (settings is null || !settings.FeatureBuddy || !settings.BuddySpeechEnabled) return;
        if (settings.BuddyQuiet || IsQuietPeriodActive(settings)) return;

        options ??= new SpeechOptions();

        if (IsSpeakingBlocked(options.Priority)) return;

        BuddyLines.RecordRecent(text);

        if (text.Length > 100) text = text[..100];

        if (_currentSpeech is not null)
        {
            if (options.Priority > _currentSpeech.Priority || (options.Priority >= 3 && _currentSpeech.Priority >= 3))
            {
                EndSpeechImmediately(force: true);
                StartSpeech(text, options);
            }
            else
            {
                if (_speechQueue.Count < 3)
                {
                    _speechQueue.Enqueue((text, options));
                }
            }
            return;
        }

        StartSpeech(text, options);
    }

    private bool IsSpeakingBlocked(int priority)
    {
        if (_window.IsOverlayOrCardOpen || _window.IsSearchOpen) return true;
        if (_window.IsDndOrFullscreenOrFocus && priority < 3) return true;
        return false;
    }

    private void StartSpeech(string text, SpeechOptions options)
    {
        _currentSpeech = options;
        _fullSpeechText = text;
        _typewriterCharIndex = 0;

        _clockContainer.Visibility = Visibility.Collapsed;
        _speechContainer.Visibility = Visibility.Visible;
        _speechText.Text = "";

        if (text.Length > 44)
        {
            _window.SetBuddyTwoLines(true);
        }

        if (options.Mood.HasValue) _behavior.BaselineMood = options.Mood.Value;
        if (options.Accessory.HasValue) _behavior.BaselineAccessory = options.Accessory.Value;
        if (options.Animation.HasValue)
            _behavior.TriggerEvent(options.Priority, options.Animation.Value, options.Mood ?? _behavior.BaselineMood, options.Accessory ?? _behavior.BaselineAccessory, 3.0);

        _hop = 0.001;

        if (_behavior.ReduceMotion)
        {
            _speechText.Text = text;
            ScheduleSpeechHold(text);
        }
        else
        {
            _typewriterTimer.Start();
        }
    }

    private void OnTypewriterTick(object? sender, EventArgs e)
    {
        if (_currentSpeech is null)
        {
            _typewriterTimer.Stop();
            return;
        }

        if (_typewriterCharIndex < _fullSpeechText.Length)
        {
            _typewriterCharIndex++;
            _speechText.Text = _fullSpeechText[.._typewriterCharIndex];
        }
        else
        {
            _typewriterTimer.Stop();
            ScheduleSpeechHold(_fullSpeechText);
        }
    }

    private void ScheduleSpeechHold(string text)
    {
        double holdMs = Math.Max(2600, text.Length * 62.0 + 700.0);
        _speechHoldTimer.Interval = TimeSpan.FromMilliseconds(holdMs);
        _speechHoldTimer.Start();
    }

    private void OnSpeechHoldFinished(object? sender, EventArgs e)
    {
        _speechHoldTimer.Stop();
        EndSpeechImmediately(force: false);
    }

    public void EndSpeechImmediately(bool force)
    {
        _typewriterTimer.Stop();
        _speechHoldTimer.Stop();
        var completed = _currentSpeech;
        _currentSpeech = null;

        _window.SetBuddyTwoLines(false);
        _speechContainer.Visibility = Visibility.Collapsed;
        _clockContainer.Visibility = Visibility.Visible;
        _speechText.Text = "";

        completed?.OnComplete?.Invoke();

        if (!force && _speechQueue.Count > 0)
        {
            var next = _speechQueue.Dequeue();
            var delayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
            delayTimer.Tick += (_, _) =>
            {
                delayTimer.Stop();
                if (_currentSpeech is null) StartSpeech(next.Text, next.Options);
            };
            delayTimer.Start();
        }
    }

    public void SetOverride(BuddyMood mood, BuddyAccessory? accessory, int durationMs)
    {
        _behavior.TriggerEvent(2, BuddyAnimationState.IdleBreathing, mood, accessory ?? BuddyAccessory.None, durationMs / 1000.0);
    }

    public void SetBaseline(BuddyMood mood, BuddyAccessory accessory = BuddyAccessory.None)
    {
        _behavior.BaselineMood = mood;
        _behavior.BaselineAccessory = accessory;
    }

    public void OnMediaPlaybackStateChanged(WindowsDynamicBar.Services.Media.MediaInfo? media)
    {
        if (media is { IsPlaying: true } && !string.IsNullOrWhiteSpace(media.Title))
        {
            string trackKey = $"{media.Artist}|{media.Title}";
            if (_behavior.HasActiveMusicSession && _behavior.CurrentTrackKey != trackKey)
            {
                _behavior.OnMediaTrackChanged(trackKey);
            }
            else if (_behavior.CurrentMusicActivity == BuddyMusicActivity.PausedWithHeadphones)
            {
                _behavior.OnMediaResumed();
            }
            else if (!_behavior.HasActiveMusicSession)
            {
                _behavior.OnMediaPlaybackStarted(trackKey);
            }
        }
        else if (media is not null && !media.IsPlaying && !string.IsNullOrWhiteSpace(media.Title))
        {
            _behavior.OnMediaPaused();
        }
        else
        {
            _behavior.OnMediaStopped();
        }
    }

    // ---------------------------------------------------------------- Interaction

    private void OnSpriteMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_window.IsSearchOpen || _buddyContainer.Visibility != Visibility.Visible) return;
        if (e.ClickCount == 1)
        {
            _clickCount = 1;
            _clickTimer.Start();
        }
        else if (e.ClickCount == 2)
        {
            _clickTimer.Stop();
            _clickCount = 0;
            PerformJoke();
        }
        e.Handled = true;
    }

    private void OnClickDebounce(object? sender, EventArgs e)
    {
        _clickTimer.Stop();
        if (_clickCount == 1)
        {
            _clickCount = 0;
            PerformPet();
        }
    }

    private void OnSpriteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            PerformPet();
            e.Handled = true;
        }
    }

    private void PerformPet()
    {
        DateTime now = DateTime.UtcNow;
        if (!_behavior.Pet(now)) return;

        App.Settings?.Update(cfg => cfg.BuddyPets++);
        string line = BuddyLines.PickNonRepeating(BuddyLines.Pet);
        Say(line, new SpeechOptions { Priority = 2, Mood = BuddyMood.Love });
    }

    private void PerformJoke()
    {
        _behavior.TriggerEvent(2, BuddyAnimationState.Laughing, BuddyMood.Excited, BuddyAccessory.None, 2.5);
        string line = BuddyLines.PickNonRepeating(BuddyLines.Jokes);
        Say(line, new SpeechOptions { Priority = 2, Mood = BuddyMood.Excited });
    }

    public void SetOutfit(BuddyOutfit outfit)
    {
        App.Settings?.Update(s => s.BuddyOutfit = outfit);
        _behavior.CurrentOutfit = outfit;
        _behavior.TriggerEvent(2, BuddyAnimationState.Hopping, BuddyMood.Excited, BuddyAccessory.None, 1.4);
        RefreshCardIfOpen();
    }

    public void OnCardOpened()
    {
        if (_isCardOpen) return;
        _isCardOpen = true;
        EndSpeechImmediately(force: true);
        RefreshCardIfOpen();
    }

    public void OnCardClosed()
    {
        _isCardOpen = false;
    }

    public void ToggleCard()
    {
        if (_isCardOpen) CloseCard();
        else OpenCard();
    }

    public void OpenCard()
    {
        _window.OpenBuddyCard();
    }

    public void CloseCard()
    {
        _window.CloseBuddyCard();
    }

    public void RefreshCardIfOpen()
    {
        if (_cardControl is null) return;
        var s = App.Settings?.Current;
        if (s is null) return;
        _cardControl.Refresh(
            s,
            _window.IsInternetConnected,
            _window.IsBatteryCharging,
            _window.IsBatteryLow,
            _window.IsRamHigh,
            _window.IsMusicActive
        );
        _cardControl.UpdateBigSprite(_sprite.Bitmap);
    }

    public void TriggerConfettiBurst()
    {
        if (_behavior.ReduceMotion) return;
        uint[] cols = { 0xFFFF5B7A, 0xFFFFD84A, 0xFF6FC3FF, 0xFF7CF0C1, 0xFFFFFFFF };
        var rnd = new Random();
        for (int i = 0; i < 26; i++)
        {
            _particles.Add(new BuddySpriteControl.Particle
            {
                Kind = 'c',
                Color = cols[rnd.Next(cols.Length)],
                X = 8,
                Y = 6,
                Vx = (rnd.NextDouble() - 0.5) * 0.8,
                Vy = -0.15 - rnd.NextDouble() * 0.4,
                Life = 14 + rnd.Next(9)
            });
        }
    }

    private void OnSpriteRightMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_window.IsSearchOpen || _buddyContainer.Visibility != Visibility.Visible) return;
        ShowBuddyContextMenu();
        e.Handled = true;
    }

    private void ShowBuddyContextMenu()
    {
        var menu = new ContextMenu();

        var cardItem = new MenuItem { Header = "Companion Card" };
        cardItem.Click += (_, _) => ToggleCard();

        var quiet1HrItem = new MenuItem { Header = "Quiet for 1 hour" };
        quiet1HrItem.Click += (_, _) =>
        {
            App.Settings?.Update(s => s.BuddyQuietUntil = DateTime.Now.AddHours(1));
            EndSpeechImmediately(force: true);
        };

        var talkLessItem = new MenuItem { Header = "Talk less" };
        talkLessItem.Click += (_, _) =>
        {
            App.Settings?.Update(s => s.BuddyTalkFrequency = BuddyTalkFrequency.Rare);
        };

        var hideBuddyItem = new MenuItem { Header = "Hide buddy" };
        hideBuddyItem.Click += (_, _) =>
        {
            App.Settings?.Update(s => s.FeatureBuddy = false);
        };

        menu.Items.Add(cardItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quiet1HrItem);
        menu.Items.Add(talkLessItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(hideBuddyItem);

        menu.PlacementTarget = _sprite;
        menu.IsOpen = true;
    }

    private void OnTalkTimerTick(object? sender, EventArgs e)
    {
        var settings = App.Settings?.Current;
        if (settings is null || !settings.FeatureBuddy || settings.BuddyQuiet || IsQuietPeriodActive(settings) || !settings.BuddySpeechEnabled || _window.IsSearchOpen) return;

        DateTime now = DateTime.Now;
        int hour = now.Hour;

        if (hour < 5 && !settings.BuddyLateNightMessages)
        {
            RestartTalkScheduler(settings);
            return;
        }

        double roll = new Random().NextDouble();
        if (roll < 0.50)
        {
            string line = BuddyLines.GetTimeLine(now);
            BuddyMood mood = hour < 5 ? BuddyMood.Sleepy : BuddyMood.Happy;
            Say(line, new SpeechOptions { Priority = 1, Mood = mood });
        }
        else
        {
            string line = BuddyLines.PickNonRepeating(BuddyLines.Fun);
            Say(line, new SpeechOptions { Priority = 1, Mood = BuddyMood.Happy });
        }

        RestartTalkScheduler(settings);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _lifeTimer.Stop();
        _typewriterTimer.Stop();
        _speechHoldTimer.Stop();
        _talkFrequencyTimer.Stop();
        _clickTimer.Stop();

        _sprite.MouseLeftButtonDown -= OnSpriteMouseDown;
        _sprite.MouseRightButtonUp -= OnSpriteRightMouseUp;
        _sprite.KeyDown -= OnSpriteKeyDown;
    }
}
