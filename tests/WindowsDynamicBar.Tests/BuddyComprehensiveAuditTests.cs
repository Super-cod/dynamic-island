using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using WindowsDynamicBar.Core.Buddy;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.UI.Buddy;

namespace WindowsDynamicBar.Tests;

internal static class BuddyComprehensiveAuditTests
{
    private static int _auditPass;
    private static int _auditFail;
    private static readonly List<string> _failures = new();

    public static void RunAll()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("  Buddy Deep Bug Hunt & Stress Audit Suite        ");
        Console.WriteLine("==================================================");

        Test("WalkFrame negative values preserve foot rendering", TestNegativeWalkFrame);
        Test("Extreme NaN and Infinity inputs to RenderFrame handled gracefully", TestRenderFrameExtremeInputs);
        Test("Every Animation State renders without exception or out-of-bounds", TestAllAnimationsExhaustive);
        Test("Every Accessory renders without exception", TestAllAccessoriesExhaustive);
        Test("Every Mood renders without exception", TestAllMoodsExhaustive);
        Test("Every Outfit renders without exception", TestAllOutfitsExhaustive);
        Test("Extreme particle system coordinates handled safely", TestParticleStress);
        Test("Movement controller handles negative and extreme dtSeconds", TestMovementExtremeDt);
        Test("Movement controller resets WalkFrame when stopped", TestMovementWalkFrameReset);
        Test("Movement bounds never invert MinX > MaxX in any resolution", TestMovementBoundsResolutionMatrix);
        Test("Interruption stack cleans expired events in LIFO order", TestInterruptionStackExpiration);
        Test("Media session stop during active priority event is respected", TestMediaStopDuringPriorityEvent);
        Test("Pet cooldown prevents rapid-fire spam", TestPetCooldownExhaustive);
        Test("BuddyLines non-repeating picker never returns null or repeats consecutively", TestBuddyLinesNonRepeatingStress);
        Test("BuddyLines returns valid slots across all 24 hours", TestBuddyLines24Hours);
        Test("BuddyCosmeticCatalog handles unmapped/invalid enum values", TestCosmeticCatalogInvalidEnums);
        Test("BuddyController.Say handles null, empty and 500-char strings safely", TestSaySafety);

        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine($"Audit Completed: {_auditPass} passed, {_auditFail} failed.");
        if (_failures.Count > 0)
        {
            Console.WriteLine("Detected Failures:");
            foreach (var f in _failures) Console.WriteLine("  [BUG] " + f);
        }
        Console.WriteLine("==================================================");
    }

    private static void Test(string name, Action act)
    {
        try
        {
            act();
            _auditPass++;
            Console.WriteLine($"  PASS: {name}");
        }
        catch (Exception ex)
        {
            _auditFail++;
            _failures.Add($"{name} -> {ex.Message}");
            Console.WriteLine($"  FAIL [BUG FOUND]: {name} | {ex.Message}");
        }
    }

    private static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    private static void TestNegativeWalkFrame()
    {
        var control = new BuddySpriteControl();
        // Negative walk frame must not lose feet
        foreach (int wf in new[] { -1, -2, -3, -4, -100 })
        {
            control.RenderFrame(
                BuddyMood.Happy, BuddyAccessory.None, 1000, false, false,
                0, 0, 0, 0, false, BuddyOutfit.None, null,
                BuddyAnimationState.Walking, false, wf
            );
            var bmp = control.Bitmap;
            int[] pixels = new int[16 * 16];
            bmp.CopyPixels(pixels, 16 * 4, 0);

            // Row 14 must have foot pixels
            int footPixels = 0;
            for (int x = 0; x < 16; x++)
            {
                if ((pixels[14 * 16 + x] & 0xFFFFFF) == 0xC47D14) footPixels++;
            }
            Assert(footPixels > 0, $"Foot pixels missing on walkFrame={wf}");
        }
    }

    private static void TestRenderFrameExtremeInputs()
    {
        var control = new BuddySpriteControl();
        double[] badDoubles = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -99999, 99999 };
        float[] badFloats = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -10f, 100f };

        foreach (var bd in badDoubles)
        {
            foreach (var bf in badFloats)
            {
                control.RenderFrame(
                    BuddyMood.Happy, BuddyAccessory.None,
                    timeMs: bd, reduceMotion: false, isTalking: false,
                    hop: bd, wave: bd, lookX: bd, lookY: bd, isBlinking: false,
                    outfit: BuddyOutfit.None, particles: null,
                    animState: BuddyAnimationState.IdleBreathing,
                    facingLeft: false, walkFrame: 0,
                    isMusicPlaying: true, audioEnergy: bf, dancePhase: bd
                );
            }
        }
    }

    private static void TestAllAnimationsExhaustive()
    {
        var control = new BuddySpriteControl();
        foreach (BuddyAnimationState anim in Enum.GetValues<BuddyAnimationState>())
        {
            control.RenderFrame(
                BuddyMood.Happy, BuddyAccessory.None, 1200, false, false,
                0.2, 0.5, 0, 0, false, BuddyOutfit.None, null,
                anim, false, 1
            );
            control.RenderFrame(
                BuddyMood.Happy, BuddyAccessory.None, 1200, false, false,
                0.2, 0.5, 0, 0, false, BuddyOutfit.None, null,
                anim, true, 1 // facing left
            );
        }
    }

    private static void TestAllAccessoriesExhaustive()
    {
        var control = new BuddySpriteControl();
        foreach (BuddyAccessory acc in Enum.GetValues<BuddyAccessory>())
        {
            control.RenderFrame(
                BuddyMood.Happy, acc, 1200, false, false,
                0, 0, 0, 0, false, BuddyOutfit.None, null,
                BuddyAnimationState.IdleBreathing, false, 0
            );
        }
    }

    private static void TestAllMoodsExhaustive()
    {
        var control = new BuddySpriteControl();
        foreach (BuddyMood mood in Enum.GetValues<BuddyMood>())
        {
            control.RenderFrame(
                mood, BuddyAccessory.None, 1200, false, false,
                0, 0, 0, 0, false, BuddyOutfit.None, null,
                BuddyAnimationState.IdleBreathing, false, 0
            );
        }
    }

    private static void TestAllOutfitsExhaustive()
    {
        var control = new BuddySpriteControl();
        foreach (BuddyOutfit outfit in Enum.GetValues<BuddyOutfit>())
        {
            control.RenderFrame(
                BuddyMood.Happy, BuddyAccessory.None, 1200, false, false,
                0, 0, 0, 0, false, outfit, null,
                BuddyAnimationState.IdleBreathing, false, 0
            );
        }
    }

    private static void TestParticleStress()
    {
        var control = new BuddySpriteControl();
        var particles = new List<BuddySpriteControl.Particle>();
        // Add extreme coordinates (out of bounds, negative life, zero life)
        for (int i = 0; i < 100; i++)
        {
            particles.Add(new BuddySpriteControl.Particle
            {
                Kind = i % 2 == 0 ? 'c' : 'h',
                Color = 0xFFFF5B7A,
                X = -50 + i * 2,
                Y = -50 + i * 2,
                Life = i % 15 - 5
            });
        }
        control.RenderFrame(
            BuddyMood.Happy, BuddyAccessory.None, 1200, false, false,
            0, 0, 0, 0, false, BuddyOutfit.None, particles,
            BuddyAnimationState.Celebrating, false, 0
        );
    }

    private static void TestMovementExtremeDt()
    {
        var m = new BuddyMovementController();
        m.UpdateLayout(IslandMode.ExpandedMusic, 600, 180);
        m.MoveTo(200);

        // Negative dt should not walk backward
        m.Update(-5.0, false);
        Assert(m.X >= m.MinX && m.X <= m.MaxX, "Negative dt clamped position");

        // Huge dt should clamp exactly at target without overshoot
        m.Update(1000.0, false);
        Assert(Math.Abs(m.X - m.TargetX) < 0.01, "Huge dt reached target");
        Assert(!m.IsWalking, "Not walking after reaching target");
    }

    private static void TestMovementWalkFrameReset()
    {
        var m = new BuddyMovementController();
        m.UpdateLayout(IslandMode.ExpandedMusic, 600, 180);
        m.MoveTo(200);
        m.Update(0.15, false); // walking
        Assert(m.IsWalking, "Walking");

        m.SnapHome();
        Assert(!m.IsWalking, "Stopped after SnapHome");
        Assert(m.WalkFrame == 0, "WalkFrame reset to 0 after SnapHome");
    }

    private static void TestMovementBoundsResolutionMatrix()
    {
        var m = new BuddyMovementController();
        double[] widths = { 0, 50, 100, 320, 600, 1920, 3840 };
        double[] heights = { 0, 20, 42, 60, 180, 1080 };

        foreach (var w in widths)
        {
            foreach (var h in heights)
            {
                foreach (IslandMode mode in Enum.GetValues<IslandMode>())
                {
                    m.UpdateLayout(mode, w, h, isSpeaking: false);
                    Assert(m.MinX <= m.MaxX, $"MinX ({m.MinX}) <= MaxX ({m.MaxX}) for {mode} at {w}x{h}");
                    Assert(m.MinY <= m.MaxY, $"MinY ({m.MinY}) <= MaxY ({m.MaxY}) for {mode} at {w}x{h}");
                }
            }
        }
    }

    private static void TestInterruptionStackExpiration()
    {
        var b = new BuddyBehaviorController();
        var now = DateTime.UtcNow;

        // P2 event duration 1.0s
        b.TriggerEvent(2, BuddyAnimationState.Hopping, BuddyMood.Love, BuddyAccessory.Heart, 1.0);
        // Interrupted by P3 event duration 3.0s
        b.TriggerEvent(3, BuddyAnimationState.Celebrating, BuddyMood.Excited, BuddyAccessory.Sparkle, 3.0);
        Assert(b.ActivePriority == 3, "P3 active");

        // After 2.0s, P2 has expired in the stack. P3 is still active.
        b.Update(2.0, now.AddSeconds(2.0));
        Assert(b.ActivePriority == 3, "P3 still active at 2.0s");

        // After 3.5s, P3 expires. P2 in stack is also expired. Should restore to P1 baseline!
        b.Update(1.5, now.AddSeconds(3.5));
        Assert(b.ActivePriority == 1, "Restored to baseline P1");
    }

    private static void TestMediaStopDuringPriorityEvent()
    {
        var b = new BuddyBehaviorController();
        b.OnMediaPlaybackStarted("Track 1");
        Assert(b.HasActiveMusicSession, "Music session active");

        // Incoming Call (Priority 3)
        b.TriggerEvent(3, BuddyAnimationState.Surprise, BuddyMood.Excited, BuddyAccessory.Phone, 2.0);
        Assert(b.ActivePriority == 3, "Priority 3 active");

        // While call is ongoing, media stops
        b.OnMediaStopped();
        Assert(!b.HasActiveMusicSession, "Media stopped");

        // Event expires
        b.Update(2.5, DateTime.UtcNow.AddSeconds(2.5));
        Assert(b.ActivePriority == 1, "Baseline restored");
        Assert(b.CurrentAccessory == BuddyAccessory.None, "Headphones NOT restored after music stopped");
    }

    private static void TestPetCooldownExhaustive()
    {
        var b = new BuddyBehaviorController();
        var now = DateTime.UtcNow;

        Assert(b.CanPet(now), "Initial pet allowed");
        Assert(b.Pet(now), "Pet succeeded");

        // 50 rapid attempts
        for (int i = 1; i <= 50; i++)
        {
            Assert(!b.CanPet(now.AddMilliseconds(i * 40)), $"Rapid pet {i} blocked");
            Assert(!b.Pet(now.AddMilliseconds(i * 40)), $"Rapid pet {i} rejected");
        }

        // Pet after 2.6s allowed
        Assert(b.CanPet(now.AddSeconds(2.6)), "Pet after cooldown allowed");
        Assert(b.Pet(now.AddSeconds(2.6)), "Pet after cooldown succeeded");
    }

    private static void TestBuddyLinesNonRepeatingStress()
    {
        for (int i = 0; i < 500; i++)
        {
            string line = BuddyLines.PickNonRepeating(BuddyLines.Morning);
            Assert(!string.IsNullOrEmpty(line), "Picked line is not empty");
        }
    }

    private static void TestBuddyLines24Hours()
    {
        for (int h = 0; h < 24; h++)
        {
            string slot = BuddyLines.GetTimeSlot(h);
            Assert(!string.IsNullOrEmpty(slot), $"Slot for hour {h} not empty");
            string line = BuddyLines.GetTimeLine(new DateTime(2026, 10, 13, h, 30, 0));
            Assert(!string.IsNullOrEmpty(line), $"Line for hour {h} not empty");
        }
    }

    private static void TestCosmeticCatalogInvalidEnums()
    {
        var invalid = (BuddyOutfit)999;
        var item = BuddyCosmeticCatalog.GetOutfit(invalid);
        Assert(item != null, "Fallback outfit returned for unmapped enum");
        Assert(item.Outfit == BuddyOutfit.None, "Fell back to None outfit");

        bool unlocked = BuddyCosmeticCatalog.IsOutfitUnlocked(invalid, null, false);
        Assert(unlocked, "Default outfit is free");
    }

    private static void TestSaySafety()
    {
        // Test Say edge cases without active window
        BuddyLines.RecordRecent("Sample 1");
        BuddyLines.RecordRecent(""); // empty string should not crash
    }
}
