using System;

namespace WindowsDynamicBar.Core.Buddy;

public enum IslandMode
{
    Collapsed,
    ExpandedMusic,
    ExpandedDashboard,
    Detail
}

public sealed class BuddyMovementController
{
    public double X { get; private set; } = 8.0;
    public double Y { get; private set; } = 5.0;
    public double TargetX { get; private set; } = 8.0;
    public double TargetY { get; private set; } = 5.0;
    public bool FacingLeft { get; private set; }
    public bool IsWalking { get; private set; }
    public int WalkFrame { get; private set; }
    public double WalkBob { get; private set; }
    public IslandMode CurrentMode { get; private set; } = IslandMode.Collapsed;

    // Boundaries for safe walking in current mode
    public double MinX { get; private set; } = 4.0;
    public double MaxX { get; private set; } = 36.0;
    public double MinY { get; private set; } = 4.0;
    public double MaxY { get; private set; } = 6.0;

    private double _walkTimer;
    private double _speed = 28.0; // DIPs per second
    private readonly Random _rng;

    public BuddyMovementController(Random? rng = null)
    {
        _rng = rng ?? new Random();
    }

    public void UpdateLayout(IslandMode mode, double islandWidth, double islandHeight, bool isSpeaking = false)
    {
        var oldMode = CurrentMode;
        CurrentMode = mode;
        switch (mode)
        {
            case IslandMode.Collapsed:
                MinX = 4.0;
                // If speaking, keep buddy strictly within far-left gutter (4..16) so it never clips text
                MaxX = isSpeaking ? 16.0 : Math.Min(48.0, Math.Max(24.0, islandWidth * 0.12));
                MinY = Math.Max(0, (islandHeight - 32.0) / 2.0);
                MaxY = MinY;
                break;

            case IslandMode.ExpandedMusic:
                MinX = 16.0;
                MaxX = Math.Max(MinX + 20.0, islandWidth - 48.0);
                MinY = Math.Max(40.0, islandHeight - 38.0);
                MaxY = MinY;
                break;

            case IslandMode.ExpandedDashboard:
                MinX = 20.0;
                MaxX = Math.Max(MinX + 20.0, islandWidth - 52.0);
                MinY = Math.Max(40.0, islandHeight - 38.0);
                MaxY = MinY;
                break;

            case IslandMode.Detail:
                MinX = 16.0;
                MaxX = Math.Max(MinX + 10.0, islandWidth - 52.0);
                MinY = Math.Max(30.0, islandHeight - 36.0);
                MaxY = MinY;
                break;
        }

        bool modeChanged = (oldMode != mode);
        // If mode changed (e.g. collapsed -> expanded), animate smoothly toward the new safe zone
        if (modeChanged)
        {
            TargetX = MinX;
            TargetY = MinY;
        }
        else
        {
            TargetX = Math.Clamp(TargetX, MinX, MaxX);
            TargetY = Math.Clamp(TargetY, MinY, MaxY);
        }
        X = Math.Clamp(X, MinX, MaxX);
        Y = Math.Clamp(Y, MinY, MaxY);
    }

    public void MoveTo(double targetX, double targetY = -1)
    {
        TargetX = Math.Clamp(targetX, MinX, MaxX);
        if (targetY >= 0)
            TargetY = Math.Clamp(targetY, MinY, MaxY);
        else
            TargetY = MinY;

        FacingLeft = TargetX < X;
    }

    public void PickRandomDestination()
    {
        if (MaxX <= MinX + 1.0) return;
        var waypoints = GetWaypoints();
        if (waypoints.Length > 0 && _rng.NextDouble() < 0.6)
        {
            double wp = waypoints[_rng.Next(waypoints.Length)];
            MoveTo(wp);
            return;
        }
        double newX = MinX + _rng.NextDouble() * (MaxX - MinX);
        MoveTo(newX);
    }

    public double[] GetWaypoints() => CurrentMode switch
    {
        IslandMode.Collapsed => new[] { MinX, Math.Min(MaxX, MinX + 8.0), MaxX },
        IslandMode.ExpandedMusic => new[] { MinX, MinX + 35.0, (MinX + MaxX) / 2.0, MaxX - 25.0, MaxX },
        IslandMode.ExpandedDashboard => new[] { MinX, (MinX + MaxX) / 2.0, MaxX },
        IslandMode.Detail => new[] { MinX, (MinX + MaxX) / 2.0, MaxX },
        _ => new[] { MinX, MaxX }
    };

    public void SnapHome()
    {
        TargetX = MinX + 4.0;
        TargetY = MinY;
        X = TargetX;
        Y = TargetY;
        IsWalking = false;
        WalkBob = 0;
    }

    public void Update(double dtSeconds, bool reduceMotion)
    {
        double dx = TargetX - X;
        double dy = TargetY - Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist > 0.8)
        {
            IsWalking = true;
            FacingLeft = dx < 0;

            if (reduceMotion)
            {
                // Immediate instant positioning
                X = TargetX;
                Y = TargetY;
                IsWalking = false;
                WalkBob = 0;
                return;
            }

            double step = _speed * dtSeconds;
            if (step >= dist)
            {
                X = TargetX;
                Y = TargetY;
                IsWalking = false;
                WalkBob = 0;
            }
            else
            {
                X += (dx / dist) * step;
                Y += (dy / dist) * step;

                _walkTimer += dtSeconds;
                WalkFrame = ((int)(_walkTimer * 7.5)) % 4;
                // 4-frame bob: frames 1 and 3 lift by 0.8 px
                WalkBob = (WalkFrame % 2 == 1) ? -0.8 : 0.0;
            }
        }
        else
        {
            X = TargetX;
            Y = TargetY;
            IsWalking = false;
            WalkBob = 0;
        }
    }
}
