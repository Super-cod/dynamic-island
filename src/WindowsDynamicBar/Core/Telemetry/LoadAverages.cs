namespace WindowsDynamicBar.Core.Telemetry;

/// <summary>
/// Bounded history of CPU-busy samples taken at a fixed interval, giving the plain average over
/// the last 1, 5 and 15 minutes. A window only reports a value once it is fully covered, so
/// nothing is extrapolated. Pure logic (no OS calls) so it can be unit-tested.
/// </summary>
public sealed class LoadAverages
{
    private readonly double[] _ring;
    private readonly int _perMinute;
    private int _next;
    private int _count;

    public LoadAverages(TimeSpan interval)
    {
        _perMinute = Math.Max(1, (int)Math.Round(60 / interval.TotalSeconds));
        _ring = new double[_perMinute * 15];
    }

    public int Capacity => _ring.Length;
    public int Count => _count;

    public void Push(double busyPercent)
    {
        _ring[_next] = Math.Clamp(busyPercent, 0, 100);
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
    }

    public (double? M1, double? M5, double? M15) Averages() =>
        (Average(_perMinute), Average(_perMinute * 5), Average(_perMinute * 15));

    private double? Average(int n)
    {
        if (_count < n) return null;
        double sum = 0;
        for (int i = 1; i <= n; i++) sum += _ring[(_next - i + _ring.Length) % _ring.Length];
        return sum / n;
    }
}
