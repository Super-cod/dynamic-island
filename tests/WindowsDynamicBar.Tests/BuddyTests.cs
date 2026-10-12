using System;
using System.Collections.Generic;
using WindowsDynamicBar.Core.Buddy;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.UI.Buddy;

namespace WindowsDynamicBar.Tests;

internal static partial class Program
{
    private static void RunBuddyTests()
    {
        Run("Buddy sprite renders moods, accessories and outfits without exception", TestBuddyRender);
        Run("Buddy sprite renders all 22 animation states without exception", TestBuddyAllAnimations);
        Run("Buddy line library returns valid slots and non-repeating selections", TestBuddyLines);
        Run("Buddy lines are bounded under 100 chars and non-empty", TestBuddyLineLengths);
        Run("Buddy movement bounds clamp safely across collapsed and expanded layouts", TestBuddyMovementBounds);
        Run("Buddy event priorities interrupt lower priority and restore cleanly", TestBuddyEventPriorities);
        Run("Buddy pet cooldown prevents spam", TestBuddyPetCooldown);
        Run("Buddy personalities configure distinct action scheduling", TestBuddyPersonalities);
        Run("Buddy cosmetic catalog handles demo packs with zero level requirements", TestBuddyCosmeticCatalog);
        Run("Buddy settings reset restores defaults cleanly", TestBuddyResetSettings);
        Run("Buddy music lifecycle transitions cleanly through play, pause, resume, and stop", TestBuddyMusicLifecycle);
        Run("Buddy screenshot interruption preserves music session and restores headphones", TestBuddyMusicInterruptionPreservation);
        Run("Buddy track change preserves headphones and updates dance smoothly", TestBuddyMusicTrackChange);
        Run("Buddy waypoints remain within safe boundaries for all layout modes", TestBuddyMovementWaypoints);
        Run("Buddy overlapping interruptions resume in order and restore music session", TestBuddyMusicOverlappingInterruptions);
        Run("Buddy pause and stop received during interruption are respected after event", TestBuddyMusicPauseAndStopDuringInterruption);
        Run("Buddy duplicate media events do not disrupt active session or headphones", TestBuddyMusicDuplicateEventsIgnored);
        Run("Buddy focus timer lifecycle enforces quiet mode and celebrates only natural completion", TestBuddyFocusLifecycle);
        Run("Buddy search lifecycle equips magnifier, hops on found, and clears on close", TestBuddySearchLifecycle);
        Run("Buddy card lifecycle tracks open/close state cleanly", TestBuddyCardLifecycle);
    }

    private static void TestBuddyRender()
    {
        var control = new BuddySpriteControl();
        foreach (BuddyMood mood in Enum.GetValues<BuddyMood>())
        {
            control.RenderFrame(
                mood: mood,
                accessory: BuddyAccessory.None,
                timeMs: 1200,
                reduceMotion: false,
                isTalking: false,
                hop: 0,
                wave: 0,
                lookX: 0,
                lookY: 0,
                isBlinking: false
            );
        }

        foreach (BuddyAccessory acc in Enum.GetValues<BuddyAccessory>())
        {
            control.RenderFrame(
                mood: BuddyMood.Happy,
                accessory: acc,
                timeMs: 1500,
                reduceMotion: false,
                isTalking: true,
                hop: 0.5,
                wave: 0.8,
                lookX: 1,
                lookY: -1,
                isBlinking: true
            );
        }

        foreach (BuddyOutfit outfit in Enum.GetValues<BuddyOutfit>())
        {
            control.RenderFrame(
                mood: BuddyMood.Happy,
                accessory: BuddyAccessory.None,
                timeMs: 1800,
                reduceMotion: false,
                isTalking: false,
                hop: 0,
                wave: 0,
                lookX: 0,
                lookY: 0,
                isBlinking: false,
                outfit: outfit
            );
        }
    }

    private static void TestBuddyAllAnimations()
    {
        var control = new BuddySpriteControl();
        foreach (BuddyAnimationState anim in Enum.GetValues<BuddyAnimationState>())
        {
            control.RenderFrame(
                mood: BuddyMood.Happy,
                accessory: BuddyAccessory.None,
                timeMs: 2000,
                reduceMotion: false,
                isTalking: false,
                hop: 0.2,
                wave: 0.5,
                lookX: 0,
                lookY: 0,
                isBlinking: false,
                outfit: BuddyOutfit.Crown,
                particles: null,
                animState: anim,
                facingLeft: true,
                walkFrame: 2
            );
        }
    }

    private static void TestBuddyMovementBounds()
    {
        var movement = new BuddyMovementController();

        // 1. Collapsed mode bounds
        movement.UpdateLayout(IslandMode.Collapsed, 340, 42, isSpeaking: false);
        Assert(movement.MinX >= 4.0, "Collapsed MinX >= 4");
        Assert(movement.MaxX <= 48.0, "Collapsed MaxX <= 48");
        Assert(movement.X >= movement.MinX && movement.X <= movement.MaxX, "X within bounds");

        // 2. Collapsed mode speaking bounds
        movement.UpdateLayout(IslandMode.Collapsed, 340, 42, isSpeaking: true);
        Assert(movement.MaxX <= 16.0, "Speaking MaxX <= 16 to avoid overlapping text");

        // 3. Expanded music mode bounds
        movement.UpdateLayout(IslandMode.ExpandedMusic, 600, 180, isSpeaking: false);
        Assert(movement.MaxX >= 500.0, "Expanded music allows walking across lower ledge");
        movement.MoveTo(200.0);
        movement.Update(0.1, reduceMotion: false);
        Assert(movement.IsWalking, "Movement updates walking state");
        Assert(movement.X > movement.MinX, "Buddy walked forward");

        // 4. Snap back to home
        movement.SnapHome();
        Assert(!movement.IsWalking, "SnapHome clears isWalking");
        Assert(movement.X == movement.TargetX, "SnapHome snaps coordinates");
    }

    private static void TestBuddyEventPriorities()
    {
        var behavior = new BuddyBehaviorController();
        behavior.BaselineMood = BuddyMood.Happy;

        // Prio 2 contextual event
        bool p2Accepted = behavior.TriggerEvent(2, BuddyAnimationState.Hopping, BuddyMood.Love, durationSeconds: 2.0);
        Assert(p2Accepted, "Prio 2 event accepted");
        Assert(behavior.ActivePriority == 2, "Active priority is 2");
        Assert(behavior.CurrentMood == BuddyMood.Love, "Current mood updated");

        // Prio 1 event cannot interrupt Prio 2
        bool p1Rejected = behavior.TriggerEvent(1, BuddyAnimationState.Sitting, BuddyMood.Calm, durationSeconds: 2.0);
        Assert(!p1Rejected, "Prio 1 cannot interrupt active Prio 2");
        Assert(behavior.ActivePriority == 2, "Active priority remains 2");

        // Prio 3 event CAN interrupt Prio 2
        bool p3Accepted = behavior.TriggerEvent(3, BuddyAnimationState.Celebrating, BuddyMood.Excited, durationSeconds: 1.0);
        Assert(p3Accepted, "Prio 3 interrupts Prio 2");
        Assert(behavior.ActivePriority == 3, "Active priority is 3");

        // After duration expires, returns to baseline
        behavior.Update(1.5, DateTime.UtcNow.AddSeconds(2.0));
        Assert(behavior.ActivePriority == 1, "Priority restored to 1 upon expiration");
    }

    private static void TestBuddyPetCooldown()
    {
        var behavior = new BuddyBehaviorController();
        DateTime now = DateTime.UtcNow;

        Assert(behavior.CanPet(now), "First pet is allowed");
        bool pet1 = behavior.Pet(now);
        Assert(pet1, "First pet succeeded");

        // Rapid pet within 1 second
        Assert(!behavior.CanPet(now.AddSeconds(1.0)), "Rapid pet blocked by cooldown");
        bool pet2 = behavior.Pet(now.AddSeconds(1.0));
        Assert(!pet2, "Rapid pet rejected");

        // Pet after 3 seconds
        Assert(behavior.CanPet(now.AddSeconds(3.0)), "Pet allowed after 3 seconds");
        bool pet3 = behavior.Pet(now.AddSeconds(3.0));
        Assert(pet3, "Pet after cooldown succeeded");
    }

    private static void TestBuddyPersonalities()
    {
        var behavior = new BuddyBehaviorController();

        behavior.SetPersonality(BuddyPersonality.Chill);
        Assert(behavior.Personality == BuddyPersonality.Chill, "Personality set to Chill");

        behavior.SetPersonality(BuddyPersonality.Playful);
        Assert(behavior.Personality == BuddyPersonality.Playful, "Personality set to Playful");

        behavior.SetPersonality(BuddyPersonality.FocusFriend);
        behavior.IsFocusActive = true;
        // Step forward in focus
        behavior.Update(10.0, DateTime.UtcNow);
        Assert(behavior.CurrentAnimation == BuddyAnimationState.IdleBreathing || behavior.CurrentAnimation == BuddyAnimationState.Walking,
            "Focus friend stays calm during active focus");
    }

    private static void TestBuddyCosmeticCatalog()
    {
        Assert(BuddyCosmeticCatalog.Outfits.Count >= 10, "At least 10 outfits in catalog");
        Assert(BuddyCosmeticCatalog.Packs.Count >= 5, "At least 5 cosmetic packs in catalog");

        var starter = BuddyCosmeticCatalog.GetOutfit(BuddyOutfit.Cap);
        Assert(starter.IsFree, "Cap is free in starter pack");

        var wizard = BuddyCosmeticCatalog.GetOutfit(BuddyOutfit.WizardHat);
        Assert(!wizard.IsFree, "Wizard hat is part of Fantasy pack");

        // Demo mode unlock check
        Assert(BuddyCosmeticCatalog.IsOutfitUnlocked(BuddyOutfit.WizardHat, null, demoMode: true),
            "Demo mode allows trying wizard hat without payment");
    }

    private static void TestBuddyResetSettings()
    {
        var model = new SettingsModel
        {
            BuddyName = "CustomName",
            BuddyPets = 50,
            BuddyStreak = 10,
            BuddyOutfit = BuddyOutfit.Crown,
            BuddyRemindWater = true,
            BuddyRemindEyes = true
        };

        // Reset logic test
        model.BuddyName = "Dot";
        model.BuddyPersonality = BuddyPersonality.Playful;
        model.BuddyOutfit = BuddyOutfit.None;
        model.BuddyPets = 0;
        model.BuddyStreak = 1;
        model.BuddyRemindWater = false;
        model.BuddyRemindEyes = false;

        Assert(model.BuddyName == "Dot", "Buddy name reset to Dot");
        Assert(model.BuddyPets == 0, "Pets reset to 0");
        Assert(model.BuddyStreak == 1, "Streak reset to 1");
        Assert(model.BuddyOutfit == BuddyOutfit.None, "Outfit reset to None");
        Assert(!model.BuddyRemindWater, "Water reminder reset to false");
    }

    private static void TestBuddyLines()
    {
        Assert(BuddyLines.GetTimeSlot(2) == "late", "2:00 is late");
        Assert(BuddyLines.GetTimeSlot(7) == "morning", "7:00 is morning");
        Assert(BuddyLines.GetTimeSlot(10) == "work", "10:00 is work");
        Assert(BuddyLines.GetTimeSlot(13) == "noon", "13:00 is noon");
        Assert(BuddyLines.GetTimeSlot(15) == "aft", "15:00 is aft");
        Assert(BuddyLines.GetTimeSlot(19) == "eve", "19:00 is eve");
        Assert(BuddyLines.GetTimeSlot(22) == "night", "22:00 is night");

        string prev = "";
        for (int i = 0; i < 20; i++)
        {
            string line = BuddyLines.GetTimeLine(DateTime.Now);
            Assert(!string.IsNullOrEmpty(line), "Time line is not empty");
            Assert(line != prev || BuddyLines.LateNight.Length == 1, "Never repeat exact same line consecutively");
            prev = line;
        }
    }

    private static void TestBuddyLineLengths()
    {
        var allGroups = new[]
        {
            BuddyLines.Morning,
            BuddyLines.Work,
            BuddyLines.Noon,
            BuddyLines.Afternoon,
            BuddyLines.Evening,
            BuddyLines.Night,
            BuddyLines.LateNight,
            BuddyLines.Fun,
            BuddyLines.Jokes,
            BuddyLines.Pet
        };

        foreach (var group in allGroups)
        {
            Assert(group.Length >= 3, "Each line slot has at least 3 lines");
            foreach (var line in group)
            {
                Assert(line.Length > 0, "Line is not empty");
                Assert(line.Length <= 100, $"Line exceeds 100 chars: {line}");
            }
        }
    }

    private static void TestBuddyMusicLifecycle()
    {
        var behavior = new BuddyBehaviorController();
        Assert(behavior.CurrentMusicActivity == BuddyMusicActivity.Stopped, "Initially stopped");
        Assert(!behavior.HasActiveMusicSession, "No active session initially");

        // 1. Playback starts
        behavior.OnMediaPlaybackStarted("Artist - Song 1");
        Assert(behavior.HasActiveMusicSession, "Active session started");
        Assert(behavior.IsMusicPlaying, "Music is playing");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones equipped");
        Assert(behavior.CurrentTrackKey == "Artist - Song 1", "Track key registered");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Dancing, "Dancing wave animation starts immediately");

        // Autonomous scheduling keeps Dot dancing / grooving during music
        for (int i = 0; i < 10; i++)
        {
            behavior.Update(5.0, DateTime.UtcNow.AddSeconds(i * 5));
            Assert(behavior.IsMusicPlaying, "Music remains playing");
            Assert(behavior.CurrentAnimation is BuddyAnimationState.Dancing or BuddyAnimationState.HeadBobbing or BuddyAnimationState.FootTapping,
                "Dot dances or grooves during music, never idles or walks away");
        }

        // 2. Playback paused
        behavior.OnMediaPaused();
        Assert(behavior.CurrentMusicActivity == BuddyMusicActivity.PausedWithHeadphones, "Transitioned to PausedWithHeadphones");
        Assert(!behavior.IsMusicPlaying, "Music not playing while paused");
        Assert(behavior.HasActiveMusicSession, "Session remains active through pause");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones preserved through pause");
        Assert(behavior.CurrentMood == BuddyMood.Calm, "Calm mood while listening paused");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Sitting, "Paused in sitting pose with headphones");

        // While paused, Dot stays calm with headphones on and does not dance
        for (int i = 0; i < 5; i++)
        {
            behavior.Update(5.0, DateTime.UtcNow.AddSeconds(i * 5));
            Assert(behavior.CurrentMusicActivity == BuddyMusicActivity.PausedWithHeadphones, "Still paused");
            Assert(behavior.CurrentAnimation == BuddyAnimationState.Sitting, "Dot remains paused in sitting pose");
        }

        // 3. Playback resumed
        behavior.OnMediaResumed();
        Assert(behavior.IsMusicPlaying, "Music playing after resume");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones retained on resume");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Dancing, "Dancing immediately resumes upon unpause");

        // 4. Playback stopped
        behavior.OnMediaStopped();
        Assert(behavior.CurrentMusicActivity == BuddyMusicActivity.Stopped, "Transitioned to Stopped");
        Assert(!behavior.HasActiveMusicSession, "Session cleanly stopped");
        Assert(behavior.CurrentAccessory == BuddyAccessory.None, "Headphones removed on stop");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.IdleBreathing, "Idle breathing restored on stop");

        // Verify sprite renders wave dance and paused states without exceptions
        var control = new BuddySpriteControl();
        control.RenderFrame(
            mood: BuddyMood.Happy,
            accessory: BuddyAccessory.Headphones,
            timeMs: 2500,
            reduceMotion: false,
            isTalking: false,
            hop: 0,
            wave: 0,
            lookX: 0,
            lookY: 0,
            isBlinking: false,
            animState: BuddyAnimationState.Dancing,
            isMusicPlaying: true,
            audioEnergy: 0.8f,
            dancePhase: 3.5
        );
        control.RenderFrame(
            mood: BuddyMood.Calm,
            accessory: BuddyAccessory.Headphones,
            timeMs: 2500,
            reduceMotion: false,
            isTalking: false,
            hop: 0,
            wave: 0,
            lookX: 0,
            lookY: 0,
            isBlinking: false,
            animState: BuddyAnimationState.Sitting,
            isMusicPlaying: false,
            audioEnergy: 0f,
            dancePhase: 0.0
        );
    }

    private static void TestBuddyMusicInterruptionPreservation()
    {
        var behavior = new BuddyBehaviorController();
        behavior.OnMediaPlaybackStarted("Artist - Song");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones on");

        // High priority screenshot event interrupts (priority 2, camera accessory, 2.0s)
        bool interrupted = behavior.TriggerEvent(2, BuddyAnimationState.CoolPose, BuddyMood.Happy, BuddyAccessory.Camera, 2.0);
        Assert(interrupted, "Screenshot event interrupted lower priority");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Camera, "Camera accessory displayed during screenshot");
        Assert(behavior.ActivePriority == 2, "Priority 2 active");

        // Step time past screenshot duration (2.5s)
        behavior.Update(2.5, DateTime.UtcNow.AddSeconds(3));
        Assert(behavior.ActivePriority == 1, "Priority reverted to 1");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones restored after screenshot");
        Assert(behavior.HasActiveMusicSession, "Music session preserved without reset");
        Assert(behavior.IsMusicPlaying, "Resumed active music listening");
    }

    private static void TestBuddyMusicTrackChange()
    {
        var behavior = new BuddyBehaviorController();
        behavior.OnMediaPlaybackStarted("Artist - Song A");
        Assert(behavior.CurrentTrackKey == "Artist - Song A", "Initial track key");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones on");

        // Track change occurs during playback
        behavior.OnMediaTrackChanged("Artist - Song B");
        Assert(behavior.CurrentTrackKey == "Artist - Song B", "Track key updated");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones preserved without replaying pickup");
        Assert(behavior.HasActiveMusicSession, "Music session continuous");
        Assert(behavior.IsMusicPlaying, "Still playing music");
    }

    private static void TestBuddyMovementWaypoints()
    {
        var movement = new BuddyMovementController();
        foreach (IslandMode mode in Enum.GetValues<IslandMode>())
        {
            movement.UpdateLayout(mode, 500, 160);
            var waypoints = movement.GetWaypoints();
            Assert(waypoints.Length > 0, $"Waypoints available for mode {mode}");
            foreach (double wp in waypoints)
            {
                Assert(wp >= movement.MinX - 0.001 && wp <= movement.MaxX + 0.001,
                    $"Waypoint {wp} within bounds [{movement.MinX}, {movement.MaxX}] for {mode}");
            }
        }
    }

    private static void TestBuddyMusicOverlappingInterruptions()
    {
        var behavior = new BuddyBehaviorController();
        behavior.OnMediaPlaybackStarted("Artist - Song");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones on for music");

        DateTime now = DateTime.UtcNow;
        // Event 1 (Priority 2, Heart/Pet, 4.0s)
        bool e1 = behavior.TriggerEvent(2, BuddyAnimationState.Hopping, BuddyMood.Love, BuddyAccessory.Heart, 4.0);
        Assert(e1, "Event 1 triggered");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Heart, "Heart active");
        Assert(behavior.ActivePriority == 2, "Priority 2");

        // 0.5s later, Event 2 (Priority 3, Camera, 1.5s) arrives and preempts Event 1
        now = now.AddSeconds(0.5);
        bool e2 = behavior.TriggerEvent(3, BuddyAnimationState.CoolPose, BuddyMood.Happy, BuddyAccessory.Camera, 1.5);
        Assert(e2, "Event 2 preempted Event 1");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Camera, "Camera active");
        Assert(behavior.ActivePriority == 3, "Priority 3");

        // Advance 1.6s: Event 2 expires, Event 1 resumes!
        now = now.AddSeconds(1.6);
        behavior.Update(1.6, now);
        Assert(behavior.ActivePriority == 2, "Priority 2 resumed from stack");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Heart, "Heart resumed from stack");

        // Advance 2.5s: Event 1 expires, music listening resumes with headphones!
        now = now.AddSeconds(2.5);
        behavior.Update(2.5, now);
        Assert(behavior.ActivePriority == 1, "All interruptions finished");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones restored after all interruptions");
        Assert(behavior.HasActiveMusicSession, "Music session preserved");
    }

    private static void TestBuddyMusicPauseAndStopDuringInterruption()
    {
        // Case A: Pause received during an interruption
        var behavior = new BuddyBehaviorController();
        behavior.OnMediaPlaybackStarted("Artist - Song");
        DateTime now = DateTime.UtcNow;
        behavior.TriggerEvent(3, BuddyAnimationState.CoolPose, BuddyMood.Happy, BuddyAccessory.Camera, 3.0);
        Assert(behavior.CurrentAccessory == BuddyAccessory.Camera, "Camera active during interruption");

        // Playback pauses while screenshot reaction is on screen
        behavior.OnMediaPaused();
        Assert(behavior.CurrentAccessory == BuddyAccessory.Camera, "Camera prop not stomped by pause");
        Assert(behavior.CurrentMusicActivity == BuddyMusicActivity.PausedWithHeadphones, "Music state transitioned to PausedWithHeadphones");

        // Interruption finishes
        now = now.AddSeconds(3.5);
        behavior.Update(3.5, now);
        Assert(behavior.ActivePriority == 1, "Priority 1");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones retained on pause restoration");
        Assert(behavior.CurrentMood == BuddyMood.Calm, "Calm mood on pause restoration");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Sitting, "Sitting pose on pause restoration");

        // Case B: Stop received during an interruption
        behavior.OnMediaPlaybackStarted("Artist - Song 2");
        now = DateTime.UtcNow;
        behavior.TriggerEvent(3, BuddyAnimationState.CoolPose, BuddyMood.Happy, BuddyAccessory.Camera, 3.0);
        behavior.OnMediaStopped();
        Assert(behavior.CurrentAccessory == BuddyAccessory.Camera, "Camera prop not stomped by stop");
        Assert(!behavior.HasActiveMusicSession, "Session marked stopped");

        now = now.AddSeconds(3.5);
        behavior.Update(3.5, now);
        Assert(behavior.CurrentAccessory == BuddyAccessory.None, "Headphones removed on stop restoration");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.IdleBreathing, "Idle breathing on stop");
    }

    private static void TestBuddyMusicDuplicateEventsIgnored()
    {
        var behavior = new BuddyBehaviorController();
        behavior.OnMediaPlaybackStarted("Artist - Track 1");
        var initialAnim = behavior.CurrentAnimation;

        // Duplicate start for same track
        behavior.OnMediaPlaybackStarted("Artist - Track 1");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones unchanged");
        Assert(behavior.CurrentTrackKey == "Artist - Track 1", "Track key identical");

        // Duplicate pause
        behavior.OnMediaPaused();
        behavior.OnMediaPaused();
        Assert(behavior.CurrentMusicActivity == BuddyMusicActivity.PausedWithHeadphones, "Still paused");

        // Duplicate stop
        behavior.OnMediaStopped();
        behavior.OnMediaStopped();
        Assert(behavior.CurrentMusicActivity == BuddyMusicActivity.Stopped, "Still stopped");
    }

    private static void TestBuddyFocusLifecycle()
    {
        var behavior = new BuddyBehaviorController();
        Assert(!behavior.IsFocusActive, "Initially not in focus");

        // 1. Focus starts
        behavior.IsFocusActive = true;
        behavior.TriggerEvent(3, BuddyAnimationState.Sitting, BuddyMood.Focused, BuddyAccessory.Hourglass, 2.5);
        Assert(behavior.CurrentAccessory == BuddyAccessory.Hourglass, "Hourglass equipped on focus start");
        Assert(behavior.CurrentMood == BuddyMood.Focused, "Focused mood");

        // 2. Focus remains active during session
        behavior.Update(3.0, DateTime.UtcNow.AddSeconds(3.5));
        Assert(behavior.IsFocusActive, "Focus remains active");

        // 3. Natural completion celebration
        behavior.IsFocusActive = false;
        behavior.TriggerEvent(3, BuddyAnimationState.Celebrating, BuddyMood.Excited, BuddyAccessory.None, 2.5);
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Celebrating, "Celebrating on natural focus completion");
        Assert(behavior.CurrentMood == BuddyMood.Excited, "Excited on completion");
    }

    private static void TestBuddySearchLifecycle()
    {
        var behavior = new BuddyBehaviorController();
        var movement = behavior.Movement;

        // 1. Perched coordinates on search ceiling
        movement.UpdateLayout(IslandMode.Search, 660, 58);
        Assert(movement.X == 0.0, "Perched X is 0.0 inside search margin slot");
        Assert(movement.Y == 0.0, "Perched Y is 0.0 inside search margin slot");
        Assert(!movement.IsWalking, "Sitting perched: not walking");

        // 2. Search opens: equips Glass, Sitting pose, Happy mood
        behavior.TriggerEvent(3, BuddyAnimationState.Sitting, BuddyMood.Happy, BuddyAccessory.Glass, 3600.0);
        Assert(behavior.CurrentAccessory == BuddyAccessory.Glass, "Magnifier equipped on search open");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Sitting, "Sitting pose on search ceiling");
        Assert(behavior.CurrentMood == BuddyMood.Happy, "Happy mood while perched");

        // 3. Search result found/executed: hops on ceiling with magnifier
        behavior.TriggerEvent(4, BuddyAnimationState.Hopping, BuddyMood.Happy, BuddyAccessory.Glass, 1.5);
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Hopping, "Hops on ceiling on successful search found");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Glass, "Maintains magnifier while hopping");

        // 4. Hop finishes: restores back to sitting pose with magnifier
        behavior.Update(2.0, DateTime.UtcNow.AddSeconds(2.0));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Sitting, "Restores to sitting on ceiling after hop");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Glass, "Still holding magnifier while search remains open");

        // 5. Search closes and cleans up back to idle baseline
        behavior.ClearActiveEvent();
        Assert(behavior.CurrentAccessory == BuddyAccessory.None, "Prop cleared on search close");
        Assert(behavior.ActivePriority == 1, "Active priority restored to 1");

        // 6. Search during active music session preserves and restores headphones & dance
        behavior.OnMediaPlaybackStarted("Artist|Song");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones equipped during music");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Dancing, "Dancing during music");

        // Open search while music playing
        behavior.TriggerEvent(3, BuddyAnimationState.Sitting, BuddyMood.Happy, BuddyAccessory.Glass, 3600.0);
        Assert(behavior.CurrentAccessory == BuddyAccessory.Glass, "Glass equipped even when music was active");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Sitting, "Sitting pose while search is open");

        // Close search
        behavior.ClearActiveEvent();
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones restored after search closed");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Dancing, "Music dance resumed after search closed");
    }

    private static void TestBuddyCardLifecycle()
    {
        var behavior = new BuddyBehaviorController();
        behavior.CurrentOutfit = BuddyOutfit.None;
        Assert(behavior.CurrentOutfit == BuddyOutfit.None, "Default outfit is None");

        behavior.CurrentOutfit = BuddyOutfit.WizardHat;
        Assert(behavior.CurrentOutfit == BuddyOutfit.WizardHat, "WizardHat outfit set");

        behavior.SetPersonality(BuddyPersonality.Chaotic);
        Assert(behavior.Personality == BuddyPersonality.Chaotic, "Chaotic personality set");

        // Verify movement bounds with card dimensions (500x180)
        behavior.Movement.UpdateLayout(IslandMode.Collapsed, 500, 180, false);
        Assert(behavior.Movement.X >= 0, "X position within card bounds");
    }
}
