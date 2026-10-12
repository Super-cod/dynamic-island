using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowsDynamicBar.Core.Buddy;

public static class BuddyLines
{
    // Morning (05-09)
    public static readonly string[] Morning =
    {
        "Good morning! Today is going to be a good one.",
        "Rise and shine. Coffee first?",
        "Morning! I already stretched for both of us.",
        "Fresh start today! What is on your agenda?"
    };

    // Work (09-12)
    public static readonly string[] Work =
    {
        "Focus time. I believe in you.",
        "Sip some water, you have been at it a while.",
        "Small steps count. Keep going.",
        "Clear desk, clear head. You are doing great."
    };

    // Noon (12-14)
    public static readonly string[] Noon =
    {
        "Lunch time! Please eat something real.",
        "Hungry? Me too. I run on dots.",
        "Midday break time. Give your eyes a rest."
    };

    // Afternoon (14-17)
    public static readonly string[] Afternoon =
    {
        "Afternoon slump? Stand up and stretch!",
        "20-20-20: look far away for 20 seconds.",
        "Almost there. Do not forget to blink.",
        "A quick stretch works wonders right now."
    };

    // Evening (17-21)
    public static readonly string[] Evening =
    {
        "Nice work today.",
        "Evening! Anything fun planned?",
        "Time to slow down a little.",
        "Wrap up your open tabs and take it easy."
    };

    // Night (21-24)
    public static readonly string[] Night =
    {
        "It is getting late. Wrap it up soon?",
        "Night mode on. Be kind to your eyes.",
        "winding down time. You had a long day."
    };

    // Late Night (00-05) - caring, short, non-judgmental
    public static readonly string[] LateNight =
    {
        "You up? Nothing tomorrow? Maybe rest a little... take care ♥",
        "It is late. Sleep is a feature, not a bug.",
        "Go to sleep soon, okay? I will be here.",
        "Still awake? I will keep the lights low."
    };

    // Day-of-week extras
    public static readonly string Monday = "Monday. You have got this.";
    public static readonly string Friday = "It is Friday! Almost there.";
    public static readonly string Weekend = "Weekend mode. Do nothing, guilt-free.";

    // Weather lines
    public static readonly string WeatherSunny = "Sunny out there. Maybe a short walk later?";
    public static readonly string WeatherRain = "Rain coming later. Take an umbrella.";
    public static readonly string WeatherHot = "It is hot out. Stay cool and drink water.";
    public static readonly string WeatherCold = "Chilly! Wear something warm.";

    // Fun lines
    public static readonly string[] Fun =
    {
        "Boop.",
        "Hydrate! Posture check, too.",
        "Ctrl+Space is my favorite shortcut.",
        "I am not a bug, I am a feature.",
        "Fun fact: you blink less when you stare at screens.",
        "I counted your clicks. Ok, I did not.",
        "Want me to stay quiet? Just say the word in settings."
    };

    // Jokes
    public static readonly string[] Jokes =
    {
        "Why do programmers like dark mode? Light attracts bugs.",
        "There are 10 kinds of people: those who get binary, and those who do not.",
        "My favorite sport? Dot-ball."
    };

    // Pet lines
    public static readonly string[] Pet =
    {
        "Hehe.",
        "That tickles!",
        "Aww, thanks.",
        "Again!"
    };

    // Real context lines
    public static readonly string NetOffline = "The internet is gone... I am checking the cables.";
    public static readonly string NetOnline = "We are back online!";
    public static readonly string BatteryLow = "I am getting hungry... battery low. Feed me power?";
    public static readonly string BatteryCharging = "Mmm, electricity.";
    public static readonly string BatteryFull = "All charged up and ready to go!";
    public static readonly string RamHigh = "RAM is high. Something is being greedy.";
    public static readonly string MusicStart = "Ooh, nice song!";
    public static readonly string MusicStop = "Quiet again...";

    // Feature moments lines (all original, warm, light and concise)
    public static readonly string FocusStart = @"Focus mode on. I will stay quiet. Shh.";
    public static readonly string FocusEnd = @"Time! Great session. Stretch a little?";
    public static readonly string NotifDelivery = @"Delivery! A message for you.";
    public static readonly string IncomingCall = @"Ring ring! Someone is calling.";
    public static readonly string SpeedFast = @"Zoom! {0} Mb/s. Your internet is flying.";
    public static readonly string SpeedSlow = @"Hmm, {0} Mb/s. Slow like a snail today.";
    public static readonly string SpeedNormal = @"Speed test done: {0} Mb/s. Solid connection.";
    public static readonly string Screenshot = @"Click! Got your screenshot.";
    public static readonly string Copied = @"Copied!";
    public static readonly string MirrorFirstTime = @"Oh! I can see you. Looking good.";
    public static readonly string SearchOpened = @"Looking for something? I will help.";
    public static readonly string SearchFound = @"Found it!";
    public static readonly string CalendarUpcoming = @"Heads up: {0} in 10 minutes.";
    public static readonly string CalendarGeneric = @"Heads up: You have an event soon in 10 minutes.";
    public static readonly string WaterBreak = @"Water break! Take a few sips.";
    public static readonly string EyeBreak = @"20-20-20: look far away for 20 seconds.";
    public static readonly string DailyGreeting = @"Day {0} together! Thanks for coming back.";

    // Recent history buffer (keeps last 10 lines so never repeat twice in a row)
    private static readonly Queue<string> _recentLines = new(10);
    private static string _lastSelected = "";
    private static readonly Random _rng = new();

    public static string GetTimeSlot(int hour) => hour switch
    {
        < 5 => "late",
        < 9 => "morning",
        < 12 => "work",
        < 14 => "noon",
        < 17 => "aft",
        < 21 => "eve",
        _ => "night"
    };

    public static string GetTimeLine(DateTime now)
    {
        int hour = now.Hour;
        string slot = GetTimeSlot(hour);
        var candidates = new List<string>(slot switch
        {
            "late" => LateNight,
            "morning" => Morning,
            "work" => Work,
            "noon" => Noon,
            "aft" => Afternoon,
            "eve" => Evening,
            _ => Night
        });

        if (slot != "late" && slot != "night")
        {
            var dow = now.DayOfWeek;
            if (dow == DayOfWeek.Monday) candidates.Add(Monday);
            else if (dow == DayOfWeek.Friday) candidates.Add(Friday);
            else if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday) candidates.Add(Weekend);
        }

        return PickNonRepeating(candidates);
    }

    public static string PickNonRepeating(IReadOnlyList<string> list)
    {
        if (list.Count == 0) return "";
        if (list.Count == 1)
        {
            RecordRecent(list[0]);
            return list[0];
        }

        // Exclude recent lines from the last 10
        var available = list.Where(l => !_recentLines.Contains(l)).ToList();

        // If all are in recent history, at least exclude the immediate last selected
        if (available.Count == 0)
        {
            available = list.Where(l => l != _lastSelected).ToList();
            if (available.Count == 0) available = list.ToList();
        }

        string picked = available[_rng.Next(available.Count)];
        _lastSelected = picked;
        RecordRecent(picked);
        return picked;
    }

    public static void RecordRecent(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        _lastSelected = line;
        if (_recentLines.Count >= 10) _recentLines.Dequeue();
        _recentLines.Enqueue(line);
    }
}
