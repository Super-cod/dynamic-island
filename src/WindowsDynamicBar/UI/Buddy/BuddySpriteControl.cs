using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDynamicBar.Core.Buddy;

namespace WindowsDynamicBar.UI.Buddy;

public sealed class BuddySpriteControl : FrameworkElement
{
    public WriteableBitmap Bitmap => _bmp;
    private const int W = 16;
    private const int PixelCount = W * W;
    private readonly WriteableBitmap _bmp;
    private readonly uint[] _pixels = new uint[PixelCount];
    private readonly Int32Rect _rect = new(0, 0, W, W);

    // Color definitions (32-bit ARGB 0xAARRGGBB)
    private const uint C_BODY = 0xFFFFCF4D;
    private const uint C_SHADE = 0xFFF2A92E;
    private const uint C_FOOT = 0xFFC47D14;
    private const uint C_EYE = 0xFF1B1308;
    private const uint C_CHEEK = 0xFFFF8F8F;
    private const uint C_CHEEK_COLD = 0xFF8FD0FF;
    private const uint C_MOUTH = 0xFF7A1F2B;
    private const uint C_TONGUE = 0xFFFF7B8A;
    private const uint C_HP = 0xFF5B6CFF;
    private const uint C_HP_BAND = 0xFF4A5AE8;      // Vibrant visible royal blue headband
    private const uint C_HP_BAND_HI = 0xFF99B2FF;   // Headband top reflection / sheen highlight
    private const uint C_HP_METAL = 0xFFD2D8E4;     // Polished aluminum/silver gimbal hinges
    private const uint C_HP_RIM = 0xFF2A3BB8;       // Deep sapphire can rim / housing
    private const uint C_HP_CAN = 0xFF5B6CFF;       // Vibrant signature acoustic royal blue
    private const uint C_HP_HI = 0xFF99B2FF;        // Specular glossy curve highlight on outer can dome
    private const uint C_HP_PAD = 0xFF3544A8;       // Cushion clearly visible against dark bar
    private const uint C_HP_CORE = 0xFFDDE6FF;      // Concentric aluminum pivot disc
    private const uint C_HP_GLOW = 0xFF00E5FF;      // Active audio beat pulse LED
    private const uint C_SWEAT = 0xFF6FC3FF;
    private const uint C_Z = 0xFFCFD8FF;
    private const uint C_HEART = 0xFFFF5B7A;
    private const uint C_NOTE = 0xFF7CF0C1;
    private const uint C_UMB = 0xFF4AA3FF;
    private const uint C_UMB_SHAFT = 0xFFCFE6FF;
    private const uint C_WHITE = 0xFFFFFFFF;
    private const uint C_RED_ALERT = 0xFFFF6B6B;
    private const uint C_SPARKLE = 0xFFFFE853;
    private const uint C_WIZARD = 0xFF3F51B5;
    private const uint C_CYAN = 0xFF00E5FF;
    private const uint C_MAGENTA = 0xFFFF007F;

    private static readonly (int Y, int X0, int X1)[] BodyRows =
    {
        (2, 5, 10),
        (3, 4, 11),
        (4, 3, 12),
        (5, 3, 12),
        (6, 3, 12),
        (7, 3, 12),
        (8, 3, 12),
        (9, 3, 12),
        (10, 3, 12),
        (11, 3, 12),
        (12, 4, 11),
        (13, 5, 10)
    };

    public sealed class Particle
    {
        public char Kind { get; set; } // 'h' = heart, 'c' = confetti
        public uint Color { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Vx { get; set; }
        public double Vy { get; set; }
        public int Life { get; set; }
    }

    public BuddySpriteControl()
    {
        Width = 32;
        Height = 32;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        _bmp = new WriteableBitmap(W, W, 96, 96, PixelFormats.Bgra32, null);
        RenderFrame(BuddyMood.Happy, BuddyAccessory.None, 0, false, false, 0, 0, 0, 0, false);
    }

    protected override Size MeasureOverride(Size availableSize) => new(32, 32);
    protected override Size ArrangeOverride(Size finalSize) => new(32, 32);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawImage(_bmp, new Rect(0, 0, RenderSize.Width, RenderSize.Height));
    }

    public void RenderFrame(
        BuddyMood mood,
        BuddyAccessory accessory,
        double timeMs,
        bool reduceMotion,
        bool isTalking,
        double hop,
        double wave,
        double lookX,
        double lookY,
        bool isBlinking,
        BuddyOutfit outfit = BuddyOutfit.None,
        List<Particle>? particles = null,
        BuddyAnimationState animState = BuddyAnimationState.IdleBreathing,
        bool facingLeft = false,
        int walkFrame = 0)
    {
        Array.Clear(_pixels, 0, PixelCount);

        double hp = hop > 0 ? Math.Abs(Math.Sin(hop * Math.PI)) * 3.0 : 0.0;
        int by = 0;

        if (!reduceMotion)
        {
            if (animState == BuddyAnimationState.Sitting || animState == BuddyAnimationState.Resting)
                by = 2;
            else if (animState == BuddyAnimationState.Stretching)
                by = -1;
            else if (animState == BuddyAnimationState.EdgePeeking)
                by = 5;
            else if (animState == BuddyAnimationState.Dancing)
                by = (int)Math.Round(Math.Sin(timeMs / 180.0) * 1.0);
            else if (animState == BuddyAnimationState.HeadBobbing)
                by = (int)Math.Round(Math.Sin(timeMs / 140.0) * 1.2);
            else if (animState == BuddyAnimationState.FootTapping)
                by = (int)Math.Round(Math.Sin(timeMs / 200.0) * 0.5);
            else if (animState == BuddyAnimationState.Laughing)
                by = (int)Math.Round(Math.Sin(timeMs / 100.0) * 0.8);
            else
                by = (int)Math.Round(Math.Sin(timeMs / 450.0) * 0.6 - hp + (mood == BuddyMood.Sleepy ? 1.0 : 0.0));
        }

        int sx = 0;
        if (animState == BuddyAnimationState.Dancing && !reduceMotion)
        {
            sx = ((int)(timeMs / 250.0) % 2 == 0) ? -1 : 1;
        }
        else if (animState == BuddyAnimationState.Stumble && !reduceMotion)
        {
            sx = 1;
            by += 1;
        }
        else if (mood == BuddyMood.Cold && !reduceMotion)
        {
            int phase = ((int)Math.Floor(timeMs / 70.0) % 2 == 1) ? 1 : -1;
            int gate = Math.Sin(timeMs / 90.0) > 0 ? 1 : 0;
            sx = phase * gate;
        }

        void P(int x, int y, uint col, int w = 1, int h = 1)
        {
            int effectiveX = facingLeft ? (15 - (x + w - 1)) : x;
            PutRect(effectiveX + (facingLeft ? -sx : sx), y + by, col, w, h);
        }

        void Q(int x, int y, uint col, int w = 1, int h = 1)
        {
            int effectiveX = facingLeft ? (15 - (x + w - 1)) : x;
            PutRect(effectiveX, y, col, w, h);
        }

        // Antenna tip color
        uint ac = mood switch
        {
            BuddyMood.Happy or BuddyMood.Cool => 0xFF6BF08A,
            BuddyMood.Sleepy => 0xFF8EA0FF,
            BuddyMood.Sad or BuddyMood.Hungry => 0xFFFF6B6B,
            BuddyMood.Sweat => 0xFFFFB86B,
            BuddyMood.Cold => 0xFF7CC9FF,
            BuddyMood.Love => 0xFFFF7BD0,
            BuddyMood.Surprised or BuddyMood.Excited => 0xFFFFF27A,
            _ => 0xFF6BF08A
        };

        // Antenna stem and tip
        if (animState != BuddyAnimationState.EdgePeeking || by < 5)
        {
            P(8, 1, C_SHADE);
            int antennaTipX = 8 + (!reduceMotion && Math.Sin(timeMs / 300.0) > 0.5 ? 1 : 0);
            P(antennaTipX, 0, ac);
        }

        // Body rows
        foreach (var row in BodyRows)
        {
            uint col = row.Y >= 10 ? C_SHADE : C_BODY;
            P(row.X0, row.Y, col, row.X1 - row.X0 + 1, 1);
        }

        // Feet logic
        if (animState == BuddyAnimationState.Walking)
        {
            // 4-frame walk cycle
            switch (walkFrame % 4)
            {
                case 0:
                    P(4, 14, C_FOOT, 2, 1);
                    P(10, 14, C_FOOT, 2, 1);
                    break;
                case 1:
                    P(5, 14, C_FOOT, 2, 1);
                    P(9, 13, C_FOOT, 2, 1); // lifted foot
                    break;
                case 2:
                    P(6, 14, C_FOOT, 2, 1);
                    P(8, 14, C_FOOT, 2, 1);
                    break;
                case 3:
                    P(5, 13, C_FOOT, 2, 1); // lifted foot
                    P(9, 14, C_FOOT, 2, 1);
                    break;
            }
        }
        else if (animState == BuddyAnimationState.Sitting || animState == BuddyAnimationState.Resting)
        {
            P(4, 13, C_FOOT, 3, 1);
            P(9, 13, C_FOOT, 3, 1);
        }
        else if (animState == BuddyAnimationState.FootTapping)
        {
            bool tap = ((int)Math.Floor(timeMs / 180.0) % 2 == 1);
            P(5, 14, C_FOOT, 2, 1);
            P(9, tap ? 13 : 14, C_FOOT, 2, 1);
        }
        else
        {
            bool fo = hp > 1.0;
            P(5, 14, C_FOOT, 2, 1);
            P(9, 14, C_FOOT, 2, 1);
            if (fo)
            {
                P(4, 14, C_FOOT, 1, 1);
                P(11, 14, C_FOOT, 1, 1);
            }
        }

        // Arms
        if (animState == BuddyAnimationState.Stretching)
        {
            P(2, 6, C_BODY, 2, 3);
            P(12, 6, C_BODY, 2, 3);
        }
        else if (animState == BuddyAnimationState.Celebrating)
        {
            P(2, 6, C_BODY, 2, 2);
            P(1, 5, C_BODY, 2, 2);
            P(12, 6, C_BODY, 2, 2);
            P(13, 5, C_BODY, 2, 2);
        }
        else if (animState == BuddyAnimationState.Dancing)
        {
            int armSwing = (int)Math.Floor(timeMs / 200.0) % 2;
            P(2, 8 + armSwing, C_BODY, 2, 2);
            P(12, 9 - armSwing, C_BODY, 2, 2);
        }
        else
        {
            P(2, 9, C_BODY);
            P(13, 9, C_BODY);
            if (wave > 0 || animState == BuddyAnimationState.Waving)
            {
                int f = (int)Math.Floor((wave > 0 ? wave : (timeMs / 120.0)) * 8.0) % 2;
                P(13, 6 + f, C_BODY);
                P(13, 7 + f, C_BODY);
                P(14, 5 + f, C_BODY);
            }
        }

        // Cheeks
        uint cheekCol = mood == BuddyMood.Cold ? C_CHEEK_COLD : C_CHEEK;
        P(3, 9, cheekCol, 2, 1);
        P(11, 9, cheekCol, 2, 1);

        // Eyes
        int lx = (int)Math.Round(lookX);
        if (animState == BuddyAnimationState.LookingLeft) lx = -1;
        if (animState == BuddyAnimationState.LookingRight) lx = 1;
        if (animState == BuddyAnimationState.Inspecting) lookY = 0.5;

        int ly = lookY > 0.3 ? 1 : (lookY < -0.3 ? 0 : 0);
        int ex = 5 + Math.Clamp(lx, -1, 1);
        int ey = 7 + ly;

        if (mood == BuddyMood.Cool || animState == BuddyAnimationState.CoolPose || outfit == BuddyOutfit.Sunglasses)
        {
            P(4, 7, C_EYE, 8, 2);
            P(5, 7, C_WHITE, 1, 1);
        }
        else if (mood == BuddyMood.Love)
        {
            DrawHeart(P, 4, 6);
            DrawHeart(9, 6, P);
        }
        else if (mood == BuddyMood.Excited || animState == BuddyAnimationState.Laughing || (mood == BuddyMood.Happy && hp > 0))
        {
            // ^ ^ eyes
            P(4, 8, C_EYE); P(5, 7, C_EYE); P(6, 8, C_EYE);
            P(9, 8, C_EYE); P(10, 7, C_EYE); P(11, 8, C_EYE);
        }
        else if (mood == BuddyMood.Sleepy || animState == BuddyAnimationState.Sleeping || isBlinking || animState == BuddyAnimationState.Blinking)
        {
            P(5, 8, C_EYE, 2, 1);
            P(9, 8, C_EYE, 2, 1);
        }
        else if (mood == BuddyMood.Surprised || animState == BuddyAnimationState.Surprise || mood == BuddyMood.Sweat)
        {
            P(ex, 6, C_EYE, 2, 3);
            P(ex + 4, 6, C_EYE, 2, 3);
            P(ex, 6, C_WHITE);
            P(ex + 4, 6, C_WHITE);
        }
        else
        {
            // Normal crisp eyes with highlight
            P(ex, ey, C_EYE, 2, 2);
            P(ex + 4, ey, C_EYE, 2, 2);
            P(ex, ey, C_WHITE);
            P(ex + 4, ey, C_WHITE);
        }

        // Mouth
        bool mouthOpen = isTalking && !reduceMotion && ((int)Math.Floor(timeMs / 130.0) % 2 == 1);
        if (mood == BuddyMood.Sad || animState == BuddyAnimationState.SadReact || mood == BuddyMood.Cold)
        {
            P(7, 10, C_MOUTH, 2, 1);
            P(6, 11, C_MOUTH);
            P(9, 11, C_MOUTH);
        }
        else if (mood == BuddyMood.Sleepy || animState == BuddyAnimationState.Sleeping)
        {
            P(7, 11, C_MOUTH, 2, 1);
        }
        else if (mood == BuddyMood.Surprised || animState == BuddyAnimationState.Surprise || mood == BuddyMood.Hungry || mouthOpen)
        {
            P(7, 10, C_MOUTH, 2, 2);
            if (mood == BuddyMood.Hungry) P(7, 11, C_TONGUE, 2, 1);
        }
        else if (mood == BuddyMood.Excited || mood == BuddyMood.Love || animState == BuddyAnimationState.Laughing)
        {
            P(6, 10, C_MOUTH, 4, 2);
            P(7, 11, C_TONGUE, 2, 1);
        }
        else if (mood == BuddyMood.Sweat)
        {
            P(6, 11, C_MOUTH, 4, 1);
        }
        else
        {
            P(5, 10, C_MOUTH);
            P(10, 10, C_MOUTH);
            P(6, 11, C_MOUTH, 4, 1);
        }

        // Outfits
        if (outfit == BuddyOutfit.Cap)
        {
            P(4, 2, 0xFFE5484D, 8, 1);
            P(3, 3, 0xFFE5484D, 10, 1);
            P(12, 3, 0xFFB83237, 3, 1);
        }
        else if (outfit == BuddyOutfit.Bow)
        {
            P(10, 2, 0xFFFF7BB0, 2, 2);
            P(13, 2, 0xFFFF7BB0, 2, 2);
            P(12, 3, 0xFFD9468A, 1, 1);
        }
        else if (outfit == BuddyOutfit.Scarf)
        {
            P(4, 12, 0xFFE5484D, 8, 1);
            P(10, 13, 0xFFE5484D, 2, 2);
        }
        else if (outfit == BuddyOutfit.Crown)
        {
            P(4, 2, 0xFFFFE27A, 8, 1);
            P(4, 1, 0xFFFFE27A, 1, 1);
            P(6, 1, 0xFFFFE27A, 1, 1);
            P(8, 1, 0xFFFFE27A, 1, 1);
            P(10, 1, 0xFFFFE27A, 1, 1);
            P(7, 2, 0xFFE5484D, 1, 1);
        }
        else if (outfit == BuddyOutfit.Glasses && mood != BuddyMood.Cool && animState != BuddyAnimationState.CoolPose)
        {
            uint g = 0xFF9AA6FF;
            P(4, 6, g, 4, 1);
            P(4, 9, g, 4, 1);
            P(4, 7, g, 1, 2);
            P(7, 7, g, 1, 2);
            P(8, 6, g, 4, 1);
            P(8, 9, g, 4, 1);
            P(8, 7, g, 1, 2);
            P(11, 7, g, 1, 2);
        }
        else if (outfit == BuddyOutfit.WizardHat)
        {
            P(4, 3, C_WIZARD, 8, 1);
            P(5, 2, C_WIZARD, 6, 1);
            P(6, 1, C_WIZARD, 4, 1);
            P(7, 0, C_WIZARD, 2, 1);
            P(8, 0, C_SPARKLE, 1, 1); // star tip
        }
        else if (outfit == BuddyOutfit.NeonVisor)
        {
            P(3, 7, C_CYAN, 10, 2);
            P(4, 7, C_MAGENTA, 8, 1);
            P(2, 7, C_WHITE, 1, 1);
        }
        else if (outfit == BuddyOutfit.WinterBeanie)
        {
            P(4, 3, 0xFF2E7D32, 8, 1);
            P(5, 2, 0xFF2E7D32, 6, 1);
            P(6, 1, 0xFF2E7D32, 4, 1);
            P(7, 0, C_WHITE, 2, 1); // pom-pom
        }
        else if (outfit == BuddyOutfit.PartyHat)
        {
            P(5, 3, 0xFFFF5722, 6, 1);
            P(6, 2, 0xFFFFC107, 4, 1);
            P(7, 1, 0xFF00E5FF, 2, 1);
            P(7, 0, C_SPARKLE, 2, 1);
        }

        // Accessories & Props
        if (accessory == BuddyAccessory.Headphones)
        {
            // 1. Arch Headband over the top of the head (Y=1: X=5..10, curves down Y=2: X=4 & 11, Y=3: X=3 & 12)
            P(5, 1, C_HP_BAND, 6, 1);
            P(6, 1, C_HP_BAND_HI, 4, 1); // glossy metallic/padded sheen across top

            // Arch shoulders curving down the sides of the skull
            P(4, 2, C_HP_BAND);
            P(11, 2, C_HP_BAND);
            P(3, 3, C_HP_BAND);
            P(12, 3, C_HP_BAND);

            // Aluminum / chrome slider yokes connecting headband into the earcups
            P(2, 4, C_HP_METAL);
            P(13, 4, C_HP_METAL);

            // 2. Big realistic Over-Ear Cushions & Cans (covering ears from Y=5 to Y=9)
            // Left Earcup (X: 1..3, Y: 5..9)
            P(2, 5, C_HP_RIM);
            P(3, 5, C_HP_PAD); // inner leather cushion against head

            P(1, 6, C_HP_RIM);
            P(2, 6, C_HP_HI);  // specular highlight on dome
            P(3, 6, C_HP_PAD);

            bool beat = !reduceMotion && ((int)(timeMs / 250.0) % 2 == 1);
            uint centerGlow = beat ? C_HP_GLOW : C_HP_CORE;
            P(1, 7, C_HP_RIM);
            P(2, 7, centerGlow); // center acoustic disc / active LED
            P(3, 7, C_HP_PAD);

            P(1, 8, C_HP_RIM);
            P(2, 8, C_HP_CAN);
            P(3, 8, C_HP_PAD);

            P(2, 9, C_HP_RIM);
            P(3, 9, C_HP_PAD);

            // Right Earcup (X: 12..14, Y: 5..9)
            P(12, 5, C_HP_PAD);
            P(13, 5, C_HP_RIM);

            P(12, 6, C_HP_PAD);
            P(13, 6, C_HP_HI);
            P(14, 6, C_HP_RIM);

            P(12, 7, C_HP_PAD);
            P(13, 7, centerGlow);
            P(14, 7, C_HP_RIM);

            P(12, 8, C_HP_PAD);
            P(13, 8, C_HP_CAN);
            P(14, 8, C_HP_RIM);

            P(12, 9, C_HP_PAD);
            P(13, 9, C_HP_RIM);

            // 3. Floating Animated Musical Note
            int n = !reduceMotion ? (int)Math.Floor(timeMs / 250.0) % 2 : 0;
            Q(13, 2 + n, C_NOTE, 1, 2);
            Q(14, 1 + n, C_NOTE, 1, 1);
        }
        if (accessory == BuddyAccessory.Umbrella)
        {
            Q(10, 1, C_UMB, 6, 1);
            Q(11, 0, C_UMB, 4, 1);
            Q(13, 2, C_UMB_SHAFT, 1, 6);
        }
        if (accessory == BuddyAccessory.Zzz || mood == BuddyMood.Sleepy || animState == BuddyAnimationState.Sleeping)
        {
            int z = (int)Math.Floor(timeMs / 600.0) % 3;
            Q(11 + z, 3 - z, C_Z, 3, 1);
            Q(13 + z, 4 - z, C_Z, 1, 1);
            Q(11 + z, 5 - z, C_Z, 3, 1);
        }
        if (mood == BuddyMood.Sweat || accessory == BuddyAccessory.Sweat)
        {
            int d = (int)Math.Floor(timeMs / 350.0) % 2;
            Q(12, 4 + d, C_SWEAT, 1, 2);
        }
        if (mood == BuddyMood.Hungry || accessory == BuddyAccessory.Exclamation)
        {
            Q(14, 1, C_RED_ALERT, 1, 3);
            Q(14, 5, C_RED_ALERT, 1, 1);
        }
        if (accessory == BuddyAccessory.Question)
        {
            Q(13, 1, C_SPARKLE, 2, 1);
            Q(14, 2, C_SPARKLE, 1, 1);
            Q(13, 3, C_SPARKLE, 1, 1);
            Q(13, 5, C_SPARKLE, 1, 1);
        }
        if (accessory == BuddyAccessory.Sparkle || animState == BuddyAnimationState.Inspecting || animState == BuddyAnimationState.ChasingSparkle)
        {
            int spX = animState == BuddyAnimationState.Inspecting ? 13 : 14;
            int spY = animState == BuddyAnimationState.Inspecting ? 12 : 9;
            Q(spX, spY, C_SPARKLE, 1, 1);
            Q(spX - 1, spY, C_SPARKLE, 1, 1);
            Q(spX + 1, spY, C_SPARKLE, 1, 1);
            Q(spX, spY - 1, C_SPARKLE, 1, 1);
            Q(spX, spY + 1, C_SPARKLE, 1, 1);
        }
        if (accessory == BuddyAccessory.Notes)
        {
            int n = (int)Math.Floor(timeMs / 200.0) % 2;
            Q(13, 3 + n, C_NOTE, 2, 2);
            Q(14, 2 + n, C_NOTE, 1, 1);
        }

        int wg = (int)Math.Floor(timeMs / 220.0) % 2;
        if (accessory == BuddyAccessory.Envelope)
        {
            Q(10, 9, C_WHITE, 5, 4);
            Q(10, 9, 0xFFC9C9D6, 5, 1);
            Q(11, 10, 0xFF9AA0B5, 1, 1);
            Q(12, 11, 0xFF9AA0B5, 1, 1);
            Q(13, 10, 0xFF9AA0B5, 1, 1);
        }
        else if (accessory == BuddyAccessory.Phone)
        {
            Q(12, 6, 0xFF2A2A31, 3, 5);
            Q(13, 7, 0xFF6FC3FF, 1, 3);
            Q(15, 5 + wg, 0xFFFFF27A, 1, 1);
            Q(15, 10 - wg, 0xFFFFF27A, 1, 1);
        }
        else if (accessory == BuddyAccessory.Glass)
        {
            Q(11, 4, 0xFFCFE6FF, 3, 1);
            Q(10, 5, 0xFFCFE6FF, 1, 2);
            Q(14, 5, 0xFFCFE6FF, 1, 2);
            Q(11, 7, 0xFFCFE6FF, 3, 1);
            Q(14, 8, 0xFF9AA6FF, 1, 1);
            Q(15, 9, 0xFF9AA6FF, 1, 1);
        }
        else if (accessory == BuddyAccessory.Clip)
        {
            Q(11, 8, 0xFFE8D9B0, 4, 5);
            Q(12, 7, 0xFF8A6A2A, 2, 1);
            Q(12, 9, 0xFF8A6A2A, 2, 1);
            Q(12, 11, 0xFF8A6A2A, 2, 1);
        }
        else if (accessory == BuddyAccessory.Camera)
        {
            Q(10, 9, 0xFF444444, 5, 4);
            Q(12, 8, 0xFF444444, 2, 1);
            Q(12, 10, 0xFF9FD8FF, 2, 2);
            if (!reduceMotion && (int)(timeMs % 1400.0) < 150)
            {
                for (int fy = 0; fy < W; fy++)
                {
                    for (int fx = 0; fx < W; fx++)
                    {
                        if (_pixels[fy * W + fx] != 0) _pixels[fy * W + fx] = 0xFFFFFFFF;
                    }
                }
            }
        }
        else if (accessory == BuddyAccessory.Bell)
        {
            int o = wg != 0 ? 1 : 0;
            Q(12 + o, 5, 0xFFFFD84A, 3, 3);
            Q(13 + o, 8, 0xFFFFD84A, 1, 1);
            Q(13 + o, 4, 0xFFFFD84A, 1, 1);
        }
        else if (accessory == BuddyAccessory.Hourglass)
        {
            Q(12, 4, 0xFFE8D9B0, 3, 1);
            Q(12, 10, 0xFFE8D9B0, 3, 1);
            int sandHeight = (int)Math.Floor(timeMs / 1500.0) % 3;
            if (sandHeight > 0) Q(13, 5, 0xFFF6C26B, 1, sandHeight);
            Q(13, 7, 0xFFE8D9B0, 1, 2);
            Q(12, 9, 0xFFF6C26B, 3, 1);
        }
        else if (accessory == BuddyAccessory.Drop)
        {
            Q(12, 8, 0xFF6FC3FF, 3, 5);
            Q(13, 7, C_WHITE, 1, 1);
        }
        else if (accessory == BuddyAccessory.Run)
        {
            Q(0, 8 + wg, C_WHITE, 2, 1);
            Q(1, 11 - wg, C_WHITE, 1, 1);
            Q(0, 5 + wg, C_WHITE, 1, 1);
        }
        else if (accessory == BuddyAccessory.Snail)
        {
            Q(11, 12, 0xFFC98A4A, 3, 3);
            Q(10, 14, 0xFFE6C9A0, 5, 1);
            Q(14, 12, 0xFFE6C9A0, 1, 2);
        }

        // Render particles
        if (particles is { Count: > 0 })
        {
            foreach (var p in particles)
            {
                if (p.Life <= 0) continue;
                int px = (int)Math.Round(p.X);
                int py = (int)Math.Round(p.Y);
                double alpha = Math.Clamp(p.Life / 10.0, 0.0, 1.0);
                uint alphaByte = (uint)(alpha * 255.0) << 24;

                if (p.Kind == 'c')
                {
                    uint col = (p.Color & 0x00FFFFFF) | alphaByte;
                    PutPixel(px, py, col);
                }
                else if (p.Kind == 'h')
                {
                    uint heartCol = (C_HEART & 0x00FFFFFF) | alphaByte;
                    DrawHeart(px, py, Q, heartCol);
                }
            }
        }

        // Most ticks (idle breathing) produce the same 16x16 frame. Pushing it anyway makes WPF
        // recompose the whole full-width layered window 14 times a second (measured ~3-5% CPU idle).
        if (_hasFrame && _pixels.AsSpan().SequenceEqual(_shown)) return;
        _pixels.CopyTo(_shown, 0);
        _hasFrame = true;

        // Write to WriteableBitmap
        _bmp.WritePixels(_rect, _pixels, W * 4, 0);
        InvalidateVisual();
    }

    private readonly uint[] _shown = new uint[PixelCount];
    private bool _hasFrame;

    private static void DrawHeart(Action<int, int, uint, int, int> put, int x, int y, uint? col = null)
    {
        uint c = col ?? C_HEART;
        put(x, y, c, 1, 1);
        put(x + 2, y, c, 1, 1);
        put(x, y + 1, c, 3, 1);
        put(x + 1, y + 2, c, 1, 1);
    }

    private static void DrawHeart(int x, int y, Action<int, int, uint, int, int> put, uint? col = null)
    {
        DrawHeart(put, x, y, col);
    }

    private void PutRect(int x, int y, uint col, int w, int h)
    {
        for (int dy = 0; dy < h; dy++)
        {
            int py = y + dy;
            if (py < 0 || py >= W) continue;
            for (int dx = 0; dx < w; dx++)
            {
                int px = x + dx;
                if (px < 0 || px >= W) continue;
                _pixels[py * W + px] = col;
            }
        }
    }

    private void PutPixel(int x, int y, uint col)
    {
        if (x >= 0 && x < W && y >= 0 && y < W)
        {
            _pixels[y * W + x] = col;
        }
    }
}
