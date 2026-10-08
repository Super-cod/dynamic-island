namespace WindowsDynamicBar.Core.Media;

public static class DiscRotation
{
    public const double TargetDegreesPerSecond = 45.0;

    public static (double Angle, double Omega) Step(double angle, double omega, bool playing, double dt)
    {
        if (dt <= 0) return (angle % 360.0 < 0 ? (angle % 360.0) + 360.0 : angle, omega);
        if (playing)
        {
            double converge = Math.Min(1.0, dt * 4.0);
            omega += (TargetDegreesPerSecond - omega) * converge;
        }
        else
        {
            omega *= Math.Exp(-dt * 3.0);
        }
        angle = (angle + omega * dt) % 360.0;
        if (angle < 0) angle += 360.0;
        return (angle, omega);
    }

    public static bool ShouldTrim(bool playing, double omega) => !playing && omega < 0.5;
}