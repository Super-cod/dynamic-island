namespace WindowsDynamicBar.Core.Telemetry;

public sealed class TelemetryHistory
{
    private readonly double[] _buffer;
    private readonly object _lock = new();
    private int _start;
    private int _count;

    public TelemetryHistory(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new double[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count { get { lock (_lock) return _count; } }
    public bool IsEmpty { get { lock (_lock) return _count == 0; } }
    public double Last
    {
        get
        {
            lock (_lock) return _count > 0 ? _buffer[(_start + _count - 1) % _buffer.Length] : 0;
        }
    }

    public void Push(double value)
    {
        lock (_lock)
        {
            int slot = (_start + _count) % _buffer.Length;
            _buffer[slot] = value;
            if (_count < _buffer.Length) _count++;
            else _start = (_start + 1) % _buffer.Length;
        }
    }

    public double[] Snapshot()
    {
        lock (_lock)
        {
            var result = new double[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(_start + i) % _buffer.Length];
            return result;
        }
    }

    public double Min()
    {
        lock (_lock)
        {
            if (_count == 0) return 0;
            double m = _buffer[_start];
            for (int i = 1; i < _count; i++)
                m = Math.Min(m, _buffer[(_start + i) % _buffer.Length]);
            return m;
        }
    }

    public double Max()
    {
        lock (_lock)
        {
            if (_count == 0) return 0;
            double m = _buffer[_start];
            for (int i = 1; i < _count; i++)
                m = Math.Max(m, _buffer[(_start + i) % _buffer.Length]);
            return m;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _start = 0;
            _count = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }
    }
}