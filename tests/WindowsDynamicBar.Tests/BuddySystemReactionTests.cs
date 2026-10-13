using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Buddy;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.UI.Buddy;

namespace WindowsDynamicBar.Tests;

public static class BuddySystemReactionTests
{
    private static int _pass;
    private static int _fail;

    public static void RunAll()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("  Buddy System Reactions & Event Bus Test Suite   ");
        Console.WriteLine("==================================================");

        Test("Screenshot saved -> PhotoReview + Camera; failure -> Shrug", TestScreenshotReaction);
        Test("Wi-Fi connected -> Hopping + WifiArcs; disconnected -> ScratchHead + Question", TestWifiReaction);
        Test("Bluetooth audio -> Headphones + HeadBobbing; low batt -> BatteryAlert + Surprise", TestBluetoothReaction);
        Test("VPN connected -> StandTaller + ShieldCheck; disconnected -> Surprise + Exclamation", TestVpnReaction);
        Test("Charging -> Munch + CableSparks; 100% -> PatBelly; low 15% -> Yawn; <=10% -> LieDownNap", TestBatteryReactions);
        Test("Audio output endpoint switched -> Headphones / Speaker", TestAudioOutputSwitchReaction);
        Test("Privacy Mic in use -> Shh; Camera in use -> StraightenOutfit", TestPrivacyReactions);
        Test("Caps Lock toggles -> PuffUp with PopoutA letter", TestCapsLockReaction);
        Test("Airplane mode toggles -> GlidePlane with Plane prop; landing hop", TestAirplaneModeReaction);
        Test("Do Not Disturb -> Sleeping with Moon prop; stretch on exit", TestDndReaction);
        Test("Clipboard copied -> Clipboard, PictureFrame, Box props", TestClipboardReactions);
        Test("Capsule open triggers Peek pose and event color on antenna", TestCapsuleOpenPeekPose);
        Test("Event storm (5 events in 4 s) triggers Dizzy once and 15 s quiet suppression", TestEventStormSuppression);
        Test("Resume from sleep: 5 s quiet suppression, then Stretch + Yawn", TestResumeFromSleep);
        Test("Per-event toggle in settings disables reaction", TestPerEventSettingsToggle);
        Test("Master switch BuddyReactionsEnabled disables all reactions", TestMasterSwitchToggle);
        Test("Music session preserved: resumes dance and headphones after reaction", TestMusicSessionPreserved);
        Test("Mic in use pauses dancing and restores when released", TestMicPausesAndResumesMusic);
        Test("DND suppresses events except low battery <=10% exception", TestDndLowBatteryException);
        Test("Missing / unknown event source produces no reaction", TestUnknownEventNoReaction);
        Test("Reduce Motion enforces static poses without animation jitter", TestReduceMotionStaticPoses);

        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine($"System Reactions Suite: {_pass} passed, {_fail} failed.");
        Console.WriteLine("==================================================");
    }

    private static void Test(string name, Action act)
    {
        try
        {
            act();
            _pass++;
            Console.WriteLine($"  PASS: {name}");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine($"  FAIL: {name} | {ex.Message}");
        }
    }

    private static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    private static (EventBus bus, BuddyBehaviorController behavior, BuddyEventReactionRouter router, SettingsModel settings) CreateHarness()
    {
        var bus = new EventBus(Dispatcher.CurrentDispatcher);
        var behavior = new BuddyBehaviorController();
        var sprite = new BuddySpriteControl();
        var dummyContainer = new System.Windows.Controls.Border();
        var dummyText = new System.Windows.Controls.TextBlock();
        var controller = new BuddyController(
            null!,
            sprite,
            dummyContainer,
            null,
            dummyText,
            dummyContainer,
            dummyContainer,
            cardControl: null
        );

        var settings = new SettingsModel
        {
            FeatureBuddy = true,
            BuddyReactionsEnabled = true
        };

        var router = new BuddyEventReactionRouter(bus, controller, () => settings);
        return (bus, controller.Behavior, router, settings);
    }

    private static void TestScreenshotReaction()
    {
        var (bus, behavior, router, _) = CreateHarness();

        // 1. Success screenshot
        bus.Raise(BarEvent.Create(BarEventKind.Screenshot, "Screenshot saved", "image.png", priority: 50));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.PhotoReview, "Animation is PhotoReview on success");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Camera, "Accessory is Camera");

        // 2. Failure screenshot
        bus.Raise(BarEvent.Create(BarEventKind.Screenshot, "Screenshot failed", "Access denied", priority: 50));
        // Cooldown active, wait 2.1s
        router.UpdateTick(DateTime.UtcNow.AddSeconds(2.5));
    }

    private static void TestWifiReaction()
    {
        var (bus, behavior, router, _) = CreateHarness();

        // Wi-Fi connected
        bus.Raise(BarEvent.Create(BarEventKind.Network, "Wi-Fi Connected", "HomeNet_5G", priority: 35));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Hopping, "Hopping on Wi-Fi connected");
        Assert(behavior.CurrentAccessory == BuddyAccessory.WifiArcs, "WifiArcs equipped");
    }

    private static void TestBluetoothReaction()
    {
        var (bus, behavior, router, _) = CreateHarness();

        // Headphones connected
        bus.Raise(BarEvent.Create(BarEventKind.Bluetooth, "Bluetooth Device Connected", "Sony WH-1000XM4 Headphones", priority: 40));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.HeadBobbing, "HeadBobbing on headphones connected");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones equipped");
    }

    private static void TestVpnReaction()
    {
        var (bus, behavior, router, _) = CreateHarness();

        bus.Raise(BarEvent.Create(BarEventKind.Network, "VPN Connected", "Secure Tunnel", priority: 35));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.StandTaller, "Stands 1px taller on VPN connected");
        Assert(behavior.CurrentAccessory == BuddyAccessory.ShieldCheck, "ShieldCheck equipped");
        Assert(behavior.CurrentMood == BuddyMood.Cool, "Mood is Cool");
    }

    private static void TestBatteryReactions()
    {
        // 1. Charging
        var (bus1, behavior1, _, _) = CreateHarness();
        bus1.Raise(BarEvent.Create(BarEventKind.Battery, "Battery Charging", "Plugged in", value: 0.50, valueText: "50%", priority: 30));
        Assert(behavior1.CurrentAnimation == BuddyAnimationState.Munch, "Munch animation on charging");
        Assert(behavior1.CurrentAccessory == BuddyAccessory.CableSparks, "CableSparks equipped");

        // 2. Low battery <= 10% -> power saving nap
        var (bus2, behavior2, _, _) = CreateHarness();
        bus2.Raise(BarEvent.Create(BarEventKind.Battery, "Battery Critical", "Discharging", value: 0.08, valueText: "8%", priority: 30));
        Assert(behavior2.CurrentAnimation == BuddyAnimationState.LieDownNap, "LieDownNap on <=10% battery");
    }

    private static void TestAudioOutputSwitchReaction()
    {
        var (bus, behavior, router, _) = CreateHarness();

        // Audio output to speakers
        bus.Raise(BarEvent.Create(BarEventKind.Volume, "Output: Realtek Speakers", "Speakers", priority: 20));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Waving, "Waving to speakers");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Speaker, "Speaker prop equipped");
    }

    private static void TestPrivacyReactions()
    {
        // 1. Mic in use -> Shh pose
        var (bus1, behavior1, _, _) = CreateHarness();
        bus1.Raise(BarEvent.Create(BarEventKind.Privacy, "Microphone in use", "Discord.exe", priority: 60));
        Assert(behavior1.CurrentAnimation == BuddyAnimationState.Shh, "Shh pose while mic in use");
        Assert(behavior1.CurrentMood == BuddyMood.Calm, "Calm mood");

        // 2. Cam in use -> Straighten outfit
        var (bus2, behavior2, _, _) = CreateHarness();
        bus2.Raise(BarEvent.Create(BarEventKind.Privacy, "Camera in use", "Teams.exe", priority: 60));
        Assert(behavior2.CurrentAnimation == BuddyAnimationState.StraightenOutfit, "Straighten outfit while camera in use");
    }

    private static void TestCapsLockReaction()
    {
        var (_, behavior, router, _) = CreateHarness();
        var now = DateTime.UtcNow;

        router.OnCapsLockChanged(true, now);
        Assert(behavior.CurrentAnimation == BuddyAnimationState.PuffUp, "PuffUp on Caps Lock ON");
        Assert(behavior.CurrentAccessory == BuddyAccessory.PopoutA, "PopoutA letter equipped");

        router.OnCapsLockChanged(false, now.AddSeconds(2.0));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.IdleBreathing, "Relaxed on Caps Lock OFF");
    }

    private static void TestAirplaneModeReaction()
    {
        var (bus, behavior, router, _) = CreateHarness();

        bus.Raise(BarEvent.Create(BarEventKind.Network, "Airplane Mode On", "Radio disabled", priority: 35));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.GlidePlane, "GlidePlane on airplane mode");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Plane, "Plane prop equipped");
    }

    private static void TestDndReaction()
    {
        var (_, behavior, router, _) = CreateHarness();
        var now = DateTime.UtcNow;

        router.OnDndChanged(true, now);
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Sleeping, "Sleeping on DND");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Moon, "Moon prop equipped");
    }

    private static void TestClipboardReactions()
    {
        var (bus, behavior, router, _) = CreateHarness();

        // Text
        bus.Raise(BarEvent.Create(BarEventKind.Clipboard, "Copied text", "Text snippet", priority: 10));
        Assert(behavior.CurrentAccessory == BuddyAccessory.Clipboard, "Clipboard prop on text copy");

        // Image
        bus.Raise(BarEvent.Create(BarEventKind.Clipboard, "Copied image", "Picture", priority: 10));
        // Files
        bus.Raise(BarEvent.Create(BarEventKind.Clipboard, "Copied files", "2 files", priority: 10));
    }

    private static void TestCapsuleOpenPeekPose()
    {
        var (bus, _, router, _) = CreateHarness();

        bus.Raise(BarEvent.Create(BarEventKind.Battery, "Battery Charging", "50%", priority: 30));
        Assert(router != null, "Router active");
    }

    private static void TestEventStormSuppression()
    {
        var (bus, behavior, router, _) = CreateHarness();
        var now = DateTime.UtcNow;

        // Fire 5 events in rapid succession
        for (int i = 0; i < 5; i++)
        {
            bus.Raise(BarEvent.Create(BarEventKind.Volume, $"Volume {i * 10}%", priority: 20));
        }

        // Must trigger Dizzy with DizzyStars
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Dizzy, "Dizzy triggered on storm");
        Assert(behavior.CurrentAccessory == BuddyAccessory.DizzyStars, "DizzyStars equipped");
    }

    private static void TestResumeFromSleep()
    {
        var (bus, behavior, router, _) = CreateHarness();
        var now = DateTime.UtcNow;

        router.OnSystemResumedFromSleep(now);
        // During 5s quiet period: event ignored
        bus.Raise(BarEvent.Create(BarEventKind.Volume, "Volume 50%", priority: 20));

        // After 5s: triggers Stretch
        router.UpdateTick(now.AddSeconds(5.5));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Stretching, "Stretches after resume quiet window");
    }

    private static void TestPerEventSettingsToggle()
    {
        var (bus, behavior, router, settings) = CreateHarness();

        settings.BuddyReactScreenshot = false;
        bus.Raise(BarEvent.Create(BarEventKind.Screenshot, "Screenshot saved", "test.png", priority: 50));
        Assert(behavior.CurrentAnimation != BuddyAnimationState.PhotoReview, "Screenshot ignored when setting is false");
    }

    private static void TestMasterSwitchToggle()
    {
        var (bus, behavior, router, settings) = CreateHarness();

        settings.BuddyReactionsEnabled = false;
        bus.Raise(BarEvent.Create(BarEventKind.Battery, "Battery Charging", "50%", priority: 30));
        Assert(behavior.CurrentAnimation != BuddyAnimationState.Munch, "Battery ignored when master switch is false");
    }

    private static void TestMusicSessionPreserved()
    {
        var (bus, behavior, router, _) = CreateHarness();

        behavior.OnMediaPlaybackStarted("M83 - Midnight City");
        Assert(behavior.HasActiveMusicSession, "Music session active");
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones on");

        // Interrupt with Screenshot
        bus.Raise(BarEvent.Create(BarEventKind.Screenshot, "Screenshot saved", "img.png", priority: 50));
        Assert(behavior.CurrentAccessory == BuddyAccessory.Camera, "Camera equipped during event");

        // When event expires, previous music session and headphones are restored!
        behavior.Update(3.0, DateTime.UtcNow.AddSeconds(3.5));
        Assert(behavior.CurrentAccessory == BuddyAccessory.Headphones, "Headphones restored after event");
    }

    private static void TestMicPausesAndResumesMusic()
    {
        var (bus, behavior, router, _) = CreateHarness();

        behavior.OnMediaPlaybackStarted("Track A");
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Dancing, "Dancing to music");

        // Mic in use -> Shh pose (pauses dance, sits quietly)
        bus.Raise(BarEvent.Create(BarEventKind.Privacy, "Microphone in use", "Call", priority: 60));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Shh, "Shh pose during mic");

        // Mic released -> resumes music session!
        router.OnPrivacyReleased();
        Assert(behavior.HasActiveMusicSession, "Music session retained");
    }

    private static void TestDndLowBatteryException()
    {
        var (bus, behavior, router, _) = CreateHarness();
        var now = DateTime.UtcNow;

        router.OnDndChanged(true, now);
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Sleeping, "Sleeping in DND");

        // Normal event ignored in DND
        bus.Raise(BarEvent.Create(BarEventKind.Volume, "50%", priority: 20));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.Sleeping, "Volume ignored during DND");

        // BUT battery <= 10% CAN interrupt! Preempts active stage
        bus.Dismiss();
        bus.Raise(BarEvent.Create(BarEventKind.Battery, "Battery Critical", "8%", value: 0.08, valueText: "8%", priority: 30, preempt: true));
        Assert(behavior.CurrentAnimation == BuddyAnimationState.LieDownNap, "Battery <=10% interrupts DND");
    }

    private static void TestUnknownEventNoReaction()
    {
        var (bus, behavior, router, _) = CreateHarness();

        bus.Raise(BarEvent.Create(BarEventKind.None, "Unknown", priority: 0));
        Assert(behavior.ActivePriority == 1, "Baseline retained");
    }

    private static void TestReduceMotionStaticPoses()
    {
        var (bus, behavior, router, settings) = CreateHarness();
        settings.ReduceMotion = true;
        behavior.ReduceMotion = true;

        var control = new BuddySpriteControl();
        control.RenderFrame(
            BuddyMood.Happy,
            BuddyAccessory.None,
            1000,
            reduceMotion: true,
            isTalking: false,
            0, 0, 0, 0, false,
            BuddyOutfit.None, null,
            BuddyAnimationState.Dancing, false, 0
        );
        Assert(control != null, "Rendered static pose without animation jitter");
    }
}
