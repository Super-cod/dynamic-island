using System;
using System.Collections.Generic;

namespace WindowsDynamicBar.Core.Buddy;

public enum BuddyMood
{
    Happy,
    Curious,
    Excited,
    Sleepy,
    Surprised,
    Sad,
    Focused,
    Cool,
    Calm,
    Sweat,
    Cold,
    Love,
    Hungry
}

public enum BuddyAnimationState
{
    IdleBreathing,
    Blinking,
    LookingLeft,
    LookingRight,
    Walking,
    Turning,
    Waving,
    Hopping,
    Sitting,
    Sleeping,
    Stretching,
    Dancing,
    Inspecting,
    Surprise,
    SadReact,
    CoolPose,
    Laughing,
    Stumble,
    EdgePeeking,
    ChasingSparkle,
    Celebrating,
    Resting,
    HeadBobbing,
    FootTapping,
    PhotoReview,
    Treadmill,
    Peek,
    Shrug,
    ScratchHead,
    StandTaller,
    Munch,
    PatBelly,
    LieDownNap,
    Shh,
    StraightenOutfit,
    PuffUp,
    GlidePlane,
    Dizzy,
    Yawn
}

public enum BuddyMusicActivity
{
    Stopped,
    Starting,
    HeadphonesOn,
    HeadBobbing,
    Dancing,
    EnergeticDance,
    RestingWithHeadphones,
    PausedWithHeadphones,
    Resuming,
    RemovingHeadphones
}

public enum BuddyAccessory
{
    None,
    Headphones,
    Umbrella,
    Zzz,
    Envelope,
    Phone,
    Glass,
    Clip,
    Camera,
    Bell,
    Hourglass,
    Drop,
    Run,
    Snail,
    Notes,
    Sparkle,
    Heart,
    Sweat,
    Question,
    Exclamation,
    PhonePhoto,
    WifiArcs,
    ShieldCheck,
    Sunglasses,
    CableSparks,
    BatteryAlert,
    Speaker,
    Moon,
    Clipboard,
    PictureFrame,
    Box,
    Plane,
    PopoutA,
    DizzyStars
}

public enum BuddyOutfit
{
    None,
    Cap,
    Bow,
    Scarf,
    Crown,
    Glasses,
    Sunglasses,
    WizardHat,
    NeonVisor,
    WinterBeanie,
    PartyHat
}

public enum BuddyPersonality
{
    Chill,
    Playful,
    Sleepy,
    Chaotic,
    FocusFriend
}

public enum BuddyTalkFrequency
{
    Rare,   // 25 - 45 min
    Normal, // ~10 min
    Often   // ~3 min
}

public enum CosmeticPackId
{
    Core,
    Fantasy,
    Cyber,
    Seasonal,
    Animation
}
