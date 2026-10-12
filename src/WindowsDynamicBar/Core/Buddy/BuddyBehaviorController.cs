using System;
using System.Collections.Generic;

namespace WindowsDynamicBar.Core.Buddy;

public sealed class BuddyBehaviorController
{
    public BuddyAnimationState CurrentAnimation { get; private set; } = BuddyAnimationState.IdleBreathing;
    public BuddyMood CurrentMood { get; private set; } = BuddyMood.Happy;
    public BuddyAccessory CurrentAccessory { get; private set; } = BuddyAccessory.None;
    public BuddyOutfit CurrentOutfit { get; set; } = BuddyOutfit.None;
    public BuddyPersonality Personality { get; set; } = BuddyPersonality.Playful;
    public bool ReduceMotion { get; set; }
    public bool IsQuiet { get; set; }
    public bool IsFocusActive { get; set; }

    // Persistent Music Lifecycle State Machine
    public BuddyMusicActivity CurrentMusicActivity { get; private set; } = BuddyMusicActivity.Stopped;
    public string CurrentTrackKey { get; private set; } = "";
    public bool IsMusicPlaying =>
        CurrentMusicActivity is BuddyMusicActivity.Starting
            or BuddyMusicActivity.HeadphonesOn
            or BuddyMusicActivity.HeadBobbing
            or BuddyMusicActivity.Dancing
            or BuddyMusicActivity.EnergeticDance
            or BuddyMusicActivity.RestingWithHeadphones;
    public bool HasActiveMusicSession =>
        CurrentMusicActivity != BuddyMusicActivity.Stopped && CurrentMusicActivity != BuddyMusicActivity.RemovingHeadphones;
    private BuddyMusicActivity _savedMusicActivity = BuddyMusicActivity.Stopped;
    private BuddyAccessory _savedMusicAccessory = BuddyAccessory.None;
    private readonly Stack<BuddyActiveEvent> _interruptionStack = new();

    public sealed class BuddyActiveEvent
    {
        public int Priority { get; init; }
        public BuddyAnimationState Animation { get; init; }
        public BuddyMood Mood { get; init; }
        public BuddyAccessory Accessory { get; init; }
        public DateTime ExpiryTime { get; init; }
    }

    // Event & Priority Management
    public int ActivePriority { get; private set; } = 1;
    public DateTime PriorityUntil { get; private set; } = DateTime.MinValue;

    // Baseline fallback states
    public BuddyMood BaselineMood { get; set; } = BuddyMood.Happy;
    public BuddyAccessory BaselineAccessory { get; set; } = BuddyAccessory.None;

    // Timing and cooldowns
    private readonly Random _rng;
    private double _actionTimer;
    private double _actionDuration = 3.0;
    private double _nextActionCooldown;
    private DateTime _lastPetTime = DateTime.MinValue;

    // Movement reference
    public BuddyMovementController Movement { get; }

    // Animation history to avoid immediate repetition
    private BuddyAnimationState _previousAnimation = BuddyAnimationState.IdleBreathing;

    public BuddyBehaviorController(Random? rng = null, BuddyMovementController? movement = null)
    {
        _rng = rng ?? new Random();
        Movement = movement ?? new BuddyMovementController(_rng);
        ScheduleNextAction();
    }

    public void SetPersonality(BuddyPersonality personality)
    {
        Personality = personality;
        ScheduleNextAction();
    }

    public bool TriggerEvent(
        int priority,
        BuddyAnimationState animation,
        BuddyMood mood,
        BuddyAccessory accessory = BuddyAccessory.None,
        double durationSeconds = 3.0)
    {
        DateTime now = DateTime.UtcNow;
        if (priority < ActivePriority && now < PriorityUntil)
        {
            // Lower priority cannot interrupt active higher priority
            return false;
        }

        if (ActivePriority > 1 && priority > ActivePriority && now < PriorityUntil)
        {
            _interruptionStack.Push(new BuddyActiveEvent
            {
                Priority = ActivePriority,
                Animation = CurrentAnimation,
                Mood = CurrentMood,
                Accessory = CurrentAccessory,
                ExpiryTime = PriorityUntil
            });
        }
        else if (priority > 1 && HasActiveMusicSession)
        {
            _savedMusicActivity = CurrentMusicActivity;
            _savedMusicAccessory = CurrentAccessory == BuddyAccessory.Headphones ? BuddyAccessory.Headphones : BaselineAccessory;
        }

        ActivePriority = priority;
        PriorityUntil = now.AddSeconds(durationSeconds);

        CurrentAnimation = animation;
        CurrentMood = mood;
        CurrentAccessory = accessory;
        _actionTimer = 0;
        _actionDuration = durationSeconds;
        return true;
    }

    public bool CanPet(DateTime now) =>
        now - _lastPetTime >= TimeSpan.FromSeconds(2.5);

    public bool Pet(DateTime now)
    {
        if (!CanPet(now)) return false;
        _lastPetTime = now;
        TriggerEvent(2, BuddyAnimationState.Hopping, BuddyMood.Love, BuddyAccessory.Heart, 2.4);
        return true;
    }

    public void Update(double dtSeconds, DateTime now)
    {
        Movement.Update(dtSeconds, ReduceMotion);

        // Check if high-priority event is active
        if (ActivePriority > 1 && now < PriorityUntil)
        {
            _actionTimer += dtSeconds;
            if (_actionTimer >= _actionDuration)
            {
                PopInterruptionOrRestore(now);
            }
            return;
        }

        // Priority expired
        if (ActivePriority > 1 && now >= PriorityUntil)
        {
            PopInterruptionOrRestore(now);
        }

        // Autonomous idle scheduling
        _actionTimer += dtSeconds;
        if (_actionTimer >= _actionDuration + _nextActionCooldown)
        {
            SelectAutonomousAction();
        }
    }

    private void RestoreIdleState()
    {
        if (HasActiveMusicSession)
        {
            CurrentAccessory = BuddyAccessory.Headphones;
            BaselineAccessory = BuddyAccessory.Headphones;
            if (IsMusicPlaying)
            {
                CurrentMood = BuddyMood.Happy;
                CurrentAnimation = _savedMusicActivity switch
                {
                    BuddyMusicActivity.Dancing or BuddyMusicActivity.EnergeticDance => BuddyAnimationState.Dancing,
                    BuddyMusicActivity.HeadBobbing => BuddyAnimationState.HeadBobbing,
                    BuddyMusicActivity.RestingWithHeadphones => BuddyAnimationState.Sitting,
                    _ => BuddyAnimationState.Dancing
                };
            }
            else
            {
                CurrentMood = BuddyMood.Calm;
                CurrentAnimation = BuddyAnimationState.Sitting;
            }
        }
        else
        {
            CurrentAnimation = Movement.IsWalking ? BuddyAnimationState.Walking : BuddyAnimationState.IdleBreathing;
            CurrentMood = BaselineMood;
            CurrentAccessory = BaselineAccessory;
        }
        ScheduleNextAction();
    }

    private void PopInterruptionOrRestore(DateTime now)
    {
        while (_interruptionStack.Count > 0)
        {
            var prev = _interruptionStack.Pop();
            if (now < prev.ExpiryTime)
            {
                ActivePriority = prev.Priority;
                CurrentAnimation = prev.Animation;
                CurrentMood = prev.Mood;
                CurrentAccessory = prev.Accessory;
                PriorityUntil = prev.ExpiryTime;
                _actionTimer = 0;
                _actionDuration = (prev.ExpiryTime - now).TotalSeconds;
                return;
            }
        }

        ActivePriority = 1;
        RestoreIdleState();
    }

    public void OnMediaPlaybackStarted(string trackKey)
    {
        if (IsMusicPlaying && CurrentTrackKey == trackKey) return;
        CurrentTrackKey = trackKey;
        if (CurrentMusicActivity == BuddyMusicActivity.PausedWithHeadphones)
        {
            OnMediaResumed();
            return;
        }

            CurrentMusicActivity = BuddyMusicActivity.Starting;
            BaselineAccessory = BuddyAccessory.Headphones;
            if (ActivePriority <= 1)
            {
                CurrentAccessory = BuddyAccessory.Headphones;
                CurrentMood = BuddyMood.Happy;
                CurrentAnimation = BuddyAnimationState.HeadBobbing;
                _actionDuration = 1.5;
            }
            CurrentMusicActivity = BuddyMusicActivity.Dancing;
    }

    public void OnMediaPaused()
    {
        if (!HasActiveMusicSession || CurrentMusicActivity == BuddyMusicActivity.PausedWithHeadphones) return;
        CurrentMusicActivity = BuddyMusicActivity.PausedWithHeadphones;
        BaselineAccessory = BuddyAccessory.Headphones;
        if (ActivePriority <= 1)
        {
            CurrentAccessory = BuddyAccessory.Headphones;
            CurrentMood = BuddyMood.Calm;
            CurrentAnimation = BuddyAnimationState.Sitting;
            _actionDuration = 4.0;
        }
    }

    public void OnMediaResumed()
    {
        if (!HasActiveMusicSession || IsMusicPlaying) return;
        CurrentMusicActivity = BuddyMusicActivity.Resuming;
        BaselineAccessory = BuddyAccessory.Headphones;
        if (ActivePriority <= 1)
        {
            CurrentAccessory = BuddyAccessory.Headphones;
            CurrentMood = BuddyMood.Happy;
            CurrentAnimation = BuddyAnimationState.HeadBobbing;
            _actionDuration = 2.0;
        }
        CurrentMusicActivity = BuddyMusicActivity.Dancing;
    }

    public void OnMediaStopped()
    {
        if (!HasActiveMusicSession) return;
        CurrentMusicActivity = BuddyMusicActivity.Stopped;
        BaselineAccessory = BuddyAccessory.None;
        if (ActivePriority <= 1)
        {
            CurrentAccessory = BuddyAccessory.None;
            RestoreIdleState();
        }
    }

    public void OnMediaTrackChanged(string newTrackKey)
    {
        if (string.Equals(CurrentTrackKey, newTrackKey, StringComparison.Ordinal) && HasActiveMusicSession) return;
        CurrentTrackKey = newTrackKey;
        if (!HasActiveMusicSession)
        {
            OnMediaPlaybackStarted(newTrackKey);
            return;
        }

        // Preserve headphones, change dance activity smoothly
        BaselineAccessory = BuddyAccessory.Headphones;
        if (ActivePriority <= 1)
        {
            CurrentAccessory = BuddyAccessory.Headphones;
            CurrentMood = BuddyMood.Excited;
            CurrentAnimation = CurrentAnimation == BuddyAnimationState.Dancing
                ? BuddyAnimationState.HeadBobbing
                : BuddyAnimationState.Dancing;
            _actionDuration = 2.5;
        }
    }

    private void ScheduleNextAction()
    {
        _actionTimer = 0;
        // Interval with jitter based on personality
        double baseInterval = Personality switch
        {
            BuddyPersonality.Playful => 8.0 + _rng.NextDouble() * 7.0,
            BuddyPersonality.Chill => 18.0 + _rng.NextDouble() * 14.0,
            BuddyPersonality.Sleepy => 15.0 + _rng.NextDouble() * 12.0,
            BuddyPersonality.Chaotic => 6.0 + _rng.NextDouble() * 7.0,
            BuddyPersonality.FocusFriend => 28.0 + _rng.NextDouble() * 20.0,
            _ => 10.0 + _rng.NextDouble() * 10.0
        };

        if (IsFocusActive) baseInterval *= 2.5;
        _nextActionCooldown = baseInterval;
    }

    private void SelectAutonomousAction()
    {
        if (IsMusicPlaying)
        {
            CurrentAccessory = BuddyAccessory.Headphones;
            BaselineAccessory = BuddyAccessory.Headphones;
            CurrentMood = BuddyMood.Happy;

            var musicActions = new List<BuddyAnimationState>
            {
                BuddyAnimationState.Dancing,
                BuddyAnimationState.HeadBobbing,
                BuddyAnimationState.FootTapping,
                BuddyAnimationState.Resting,
                BuddyAnimationState.LookingLeft,
                BuddyAnimationState.LookingRight,
                BuddyAnimationState.Walking
            };
            musicActions.RemoveAll(a => a == _previousAnimation);
            if (musicActions.Count == 0) musicActions.Add(BuddyAnimationState.Dancing);

            var musicChosen = musicActions[_rng.Next(musicActions.Count)];
            _previousAnimation = musicChosen;
            CurrentAnimation = musicChosen;

            if (musicChosen == BuddyAnimationState.Walking)
            {
                Movement.PickRandomDestination();
                _actionDuration = 2.5;
            }
            else if (musicChosen == BuddyAnimationState.Resting)
            {
                CurrentMood = BuddyMood.Calm;
                _actionDuration = 3.5;
            }
            else
            {
                _actionDuration = 3.0;
            }
            ScheduleNextAction();
            return;
        }
        else if (CurrentMusicActivity == BuddyMusicActivity.PausedWithHeadphones)
        {
            CurrentAccessory = BuddyAccessory.Headphones;
            BaselineAccessory = BuddyAccessory.Headphones;
            CurrentMood = BuddyMood.Calm;
            CurrentAnimation = BuddyAnimationState.Sitting;
            _actionDuration = 4.0;
            ScheduleNextAction();
            return;
        }

        if (IsFocusActive && Personality == BuddyPersonality.FocusFriend)
        {
            // Focus friend stays very calm during active focus
            CurrentAnimation = BuddyAnimationState.IdleBreathing;
            CurrentMood = BuddyMood.Focused;
            CurrentAccessory = BuddyAccessory.None;
            _actionDuration = 5.0;
            ScheduleNextAction();
            return;
        }

        var candidateActions = new List<BuddyAnimationState>();

        // Movement is always a candidate
        candidateActions.Add(BuddyAnimationState.Walking);

        switch (Personality)
        {
            case BuddyPersonality.Playful:
                candidateActions.Add(BuddyAnimationState.Hopping);
                candidateActions.Add(BuddyAnimationState.Waving);
                candidateActions.Add(BuddyAnimationState.Dancing);
                candidateActions.Add(BuddyAnimationState.ChasingSparkle);
                candidateActions.Add(BuddyAnimationState.Celebrating);
                candidateActions.Add(BuddyAnimationState.Inspecting);
                break;

            case BuddyPersonality.Chill:
                candidateActions.Add(BuddyAnimationState.Sitting);
                candidateActions.Add(BuddyAnimationState.Resting);
                candidateActions.Add(BuddyAnimationState.LookingLeft);
                candidateActions.Add(BuddyAnimationState.LookingRight);
                candidateActions.Add(BuddyAnimationState.Stretching);
                candidateActions.Add(BuddyAnimationState.CoolPose);
                break;

            case BuddyPersonality.Sleepy:
                candidateActions.Add(BuddyAnimationState.Sleeping);
                candidateActions.Add(BuddyAnimationState.Sitting);
                candidateActions.Add(BuddyAnimationState.Stretching);
                candidateActions.Add(BuddyAnimationState.Resting);
                break;

            case BuddyPersonality.Chaotic:
                candidateActions.Add(BuddyAnimationState.Hopping);
                candidateActions.Add(BuddyAnimationState.Dancing);
                candidateActions.Add(BuddyAnimationState.Stumble);
                candidateActions.Add(BuddyAnimationState.EdgePeeking);
                candidateActions.Add(BuddyAnimationState.Surprise);
                candidateActions.Add(BuddyAnimationState.Laughing);
                candidateActions.Add(BuddyAnimationState.ChasingSparkle);
                break;

            default:
                candidateActions.Add(BuddyAnimationState.Waving);
                candidateActions.Add(BuddyAnimationState.Hopping);
                candidateActions.Add(BuddyAnimationState.Sitting);
                candidateActions.Add(BuddyAnimationState.LookingLeft);
                candidateActions.Add(BuddyAnimationState.LookingRight);
                break;
        }

        // Avoid repeating the same animation consecutively
        candidateActions.RemoveAll(a => a == _previousAnimation);
        if (candidateActions.Count == 0) candidateActions.Add(BuddyAnimationState.IdleBreathing);

        var chosen = candidateActions[_rng.Next(candidateActions.Count)];
        _previousAnimation = chosen;
        CurrentAnimation = chosen;

        // Apply action parameters
        switch (chosen)
        {
            case BuddyAnimationState.Walking:
                Movement.PickRandomDestination();
                _actionDuration = 2.5;
                CurrentMood = BaselineMood;
                CurrentAccessory = BaselineAccessory;
                break;

            case BuddyAnimationState.Waving:
                CurrentMood = BuddyMood.Happy;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 1.6;
                break;

            case BuddyAnimationState.Hopping:
                CurrentMood = BuddyMood.Excited;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 1.4;
                break;

            case BuddyAnimationState.Sitting:
                CurrentMood = BuddyMood.Calm;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 4.0;
                break;

            case BuddyAnimationState.Sleeping:
                CurrentMood = BuddyMood.Sleepy;
                CurrentAccessory = BuddyAccessory.Zzz;
                _actionDuration = 6.0;
                break;

            case BuddyAnimationState.Stretching:
                CurrentMood = BuddyMood.Happy;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 2.2;
                break;

            case BuddyAnimationState.Dancing:
                CurrentMood = BuddyMood.Excited;
                CurrentAccessory = BuddyAccessory.Notes;
                _actionDuration = 3.2;
                break;

            case BuddyAnimationState.Inspecting:
                CurrentMood = BuddyMood.Curious;
                CurrentAccessory = BuddyAccessory.Sparkle;
                _actionDuration = 2.8;
                break;

            case BuddyAnimationState.ChasingSparkle:
                CurrentMood = BuddyMood.Excited;
                CurrentAccessory = BuddyAccessory.Sparkle;
                Movement.PickRandomDestination();
                _actionDuration = 3.0;
                break;

            case BuddyAnimationState.Celebrating:
                CurrentMood = BuddyMood.Excited;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 2.5;
                break;

            case BuddyAnimationState.Stumble:
                CurrentMood = BuddyMood.Surprised;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 1.8;
                break;

            case BuddyAnimationState.EdgePeeking:
                CurrentMood = BuddyMood.Curious;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 3.0;
                break;

            case BuddyAnimationState.CoolPose:
                CurrentMood = BuddyMood.Cool;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 3.5;
                break;

            case BuddyAnimationState.Laughing:
                CurrentMood = BuddyMood.Excited;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 2.0;
                break;

            default:
                CurrentMood = BaselineMood;
                CurrentAccessory = BaselineAccessory;
                _actionDuration = 2.0;
                break;
        }

        ScheduleNextAction();
    }
}
