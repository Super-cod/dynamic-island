using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Net;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Services.Settings;
using WindowsDynamicBar.Services.SystemInfo;

namespace WindowsDynamicBar.Services.SystemMonitor;

/// <summary>Phase of the on-demand speed test.</summary>
public enum NetTestPhase { Idle, Ping, Download, Upload, Done, Error, Offline }

/// <summary>Latency probe targets shown in the net panel tiles.</summary>
public enum NetTarget { Gateway, Dns, Cloudflare, Google }

/// <summary>Immutable live snapshot of adapter activity as shown in the net panel.</summary>
public sealed class NetSnapshot
{
    public double DownMbps;
    public double UpMbps;
    public string Kind = "";
    public string? Name;
    public int SignalBars = -1;
    public int? RssiDbm;
    public double? LinkMbps;
    public bool Connected;
    public bool Internet;
    public long TodayRxBytes;
    public long TodayTxBytes;
    public bool HasAdapterData;
}

/// <summary>Mutable speed-test state; copied out under lock for the UI.</summary>
public sealed class NetSpeedState
{
    public NetTestPhase Phase = NetTestPhase.Idle;
    public double ProgressPercent;
    public double? PingMs;
    public double? JitterMs;
    public double? LossPercent;
    public double? DownMbps;
    public double? UpMbps;
    public double? LoadedMs;
    public string? Error;
}

/// <summary>
/// Samples real network throughput from the operating system, probes latency to real hosts and
/// runs an on-demand speed test against Cloudflare's public endpoints. Everything is measured -
/// nothing is fabricated; unavailable values stay null and the UI renders "—".
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    /// <summary>Fixed display full-scale for the mirrored graph &amp; icon ring (matches the demo).</summary>
    public const double GraphMaxMbps = 300.0;
    private const int PingHistoryLength = 14;
    private const double SampleSeconds = 0.5;

    private readonly SettingsService _settings;
    private readonly object _sync = new();
    private readonly System.Threading.Timer _timer;
    private readonly CancellationTokenSource _disposeCts = new();

    private readonly TelemetryHistory _down = new(240);
    private readonly TelemetryHistory _up = new(240);
    private readonly Dictionary<NetTarget, TelemetryHistory> _pingHistory = new();
    private readonly Dictionary<NetTarget, double?> _lastPing = new();

    private readonly Dictionary<string, IfaceBytes> _ifaceBytes = new();
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private DateTime _lastPingUtc = DateTime.MinValue;

    private NetSnapshot _snapshot = new();
    private NetSpeedState _test = new();

    private CancellationTokenSource? _testCts;
    private int _testRunning;   // 0/1 via Interlocked
    private DateTime _lastPersistUtc = DateTime.UtcNow;
    private long _dayRx;
    private long _dayTx;
    private string _dayDate = "";

    public event Action? Updated;

    private sealed class IfaceBytes { public long Rx; public long Tx; }

    public NetworkMonitor(SettingsService settings)
    {
        _settings = settings;
        foreach (NetTarget t in Enum.GetValues<NetTarget>())
        {
            _pingHistory[t] = new TelemetryHistory(240);
            _lastPing[t] = null;
        }
        LoadDailyTotals();
        _timer = new System.Threading.Timer(_ => Sample(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ------------------------------------------------------------------ accessors

    public NetSnapshot Snapshot { get { lock (_sync) return CloneSnapshot(_snapshot); } }
    public NetSpeedState TestState { get { lock (_sync) return CloneTest(_test); } }
    public bool TestRunning => Volatile.Read(ref _testRunning) == 1;

    public double[] DownSeries() { lock (_sync) return _down.Snapshot(); }
    public double[] UpSeries() { lock (_sync) return _up.Snapshot(); }

    public double? LastPing(NetTarget t)
    {
        lock (_sync) return _lastPing.TryGetValue(t, out var v) ? v : null;
    }

    public double[] PingSeries(NetTarget t)
    {
        lock (_sync)
        {
            var h = _pingHistory[t].Snapshot();
            if (h.Length <= PingHistoryLength) return h;
            var tail = new double[PingHistoryLength];
            Array.Copy(h, h.Length - PingHistoryLength, tail, 0, PingHistoryLength);
            return tail;
        }
    }

    public static string TargetLabel(NetTarget t) => t switch
    {
        NetTarget.Gateway => "GW",
        NetTarget.Dns => "DNS",
        NetTarget.Cloudflare => "1.1.1.1",
        NetTarget.Google => "Google",
        _ => "",
    };

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        LoadDailyTotals();
        _lastSampleUtc = DateTime.UtcNow;
        // The counters last seen may be minutes or hours old: the first sample only re-baselines,
        // otherwise everything transferred while closed shows up as one huge "speed" spike.
        _needBaseline = true;
        _timer.Change(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500));
    }

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        PersistDailyTotals(force: true);
    }

    public void Dispose()
    {
        try { CancelSpeedTest(); } catch { }
        _disposeCts.Cancel();
        _timer.Dispose();
        _disposeCts.Dispose();
        Stop();
    }

    // ------------------------------------------------------------------ sampling

    private volatile bool _needBaseline = true;
    private int _sampling;   // 0/1: a slow sample must not overlap the next 500 ms tick

    private void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var now = DateTime.UtcNow;
            double elapsed = (now - _lastSampleUtc).TotalSeconds;
            _lastSampleUtc = now;
            if (elapsed <= 0.05) elapsed = SampleSeconds;

            var info = NetworkService.Query();
            double downMbps = 0, upMbps = 0;
            bool hasAdapter = SampleAdapter(elapsed, out downMbps, out upMbps);

            var snap = new NetSnapshot
            {
                DownMbps = downMbps,
                UpMbps = upMbps,
                Kind = info.Kind,
                Name = info.Name,
                SignalBars = info.SignalBars,
                RssiDbm = info.Kind == "Wi-Fi" ? NetworkService.QueryWifiRssi() : null,
                LinkMbps = QueryLinkSpeed(),
                Connected = info.Connected,
                Internet = info.Internet,
                TodayRxBytes = Interlocked.Read(ref _dayRx),
                TodayTxBytes = Interlocked.Read(ref _dayTx),
                HasAdapterData = hasAdapter,
            };

            lock (_sync)
            {
                _snapshot = snap;
                if (hasAdapter)
                {
                    _down.Push(Math.Clamp(downMbps / GraphMaxMbps * 100.0, 0, 100));
                    _up.Push(Math.Clamp(upMbps / GraphMaxMbps * 100.0, 0, 100));
                }
            }

            if ((now - _lastPingUtc).TotalSeconds >= 1.0)
            {
                _lastPingUtc = now;
                _ = ProbeLatencyAsync();
            }

            PersistDailyTotals(force: false);
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Debug("Network sample failed", ("error", ex.Message));
        }
        finally { Interlocked.Exchange(ref _sampling, 0); }
    }

    private bool SampleAdapter(double elapsed, out double downMbps, out double upMbps)
    {
        downMbps = 0; upMbps = 0;
        bool baseline = _needBaseline;
        _needBaseline = false;
        NetworkInterface? best = null;
        long bestRx = 0, bestTx = 0;

        NetworkInterface[] ifaces;
        try { ifaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return false; }

        foreach (var ni in ifaces)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            try
            {
                var stats = ni.GetIPv4Statistics();
                long rx = stats.BytesReceived, tx = stats.BytesSent;
                string id = ni.Id;
                if (_ifaceBytes.TryGetValue(id, out var prev))
                {
                    long drx = Math.Max(0, rx - prev.Rx);
                    long dtx = Math.Max(0, tx - prev.Tx);
                    if (drx + dtx >= bestRx + bestTx)
                    {
                        bestRx = drx; bestTx = dtx; best = ni;
                    }
                }
                _ifaceBytes[id] = new IfaceBytes { Rx = rx, Tx = tx };
            }
            catch { /* interface vanished */ }
        }

        if (best is null) return false;

        Interlocked.Add(ref _dayRx, bestRx);
        Interlocked.Add(ref _dayTx, bestTx);
        if (baseline) return false;   // counted for today, but not a rate
        downMbps = bestRx * 8.0 / elapsed / 1e6;
        upMbps = bestTx * 8.0 / elapsed / 1e6;
        return true;
    }

    private double? QueryLinkSpeed()
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                n.Speed > 0);
            return ni is null ? null : ni.Speed / 1e6;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ latency probes

    private async Task ProbeLatencyAsync()
    {
        if (_disposeCts.IsCancellationRequested) return;
        var token = _disposeCts.Token;

        var tasks = new List<(NetTarget Target, Task<double?> Task)>
        {
            (NetTarget.Gateway, IcmpPingAsync(FirstGateway(), token)),
            (NetTarget.Dns, IcmpPingAsync(FirstDns(), token)),
            (NetTarget.Cloudflare, IcmpPingAsync("1.1.1.1", token)),
            (NetTarget.Google, IcmpPingAsync("8.8.8.8", token)),
        };

        await Task.WhenAll(tasks.Select(t => t.Task)).ConfigureAwait(false);

        lock (_sync)
        {
            foreach (var (target, task) in tasks)
            {
                double? ms = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                _lastPing[target] = ms;
                if (ms is not null) _pingHistory[target].Push(ms.Value);
            }
        }
        Updated?.Invoke();
    }

    private static async Task<double?> IcmpPingAsync(string? host, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 900).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success) return reply.RoundtripTime;
        }
        catch { }
        return null;
    }

    private static string? FirstGateway()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var gw = ni.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (gw is not null) return gw.Address.ToString();
            }
        }
        catch { }
        return null;
    }

    private static string? FirstDns()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var dns = ni.GetIPProperties().DnsAddresses
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (dns is not null) return dns.ToString();
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ speed test

    public void StartSpeedTest()
    {
        if (Interlocked.CompareExchange(ref _testRunning, 1, 0) != 0) return;
        _testCts = new CancellationTokenSource();
        SetTest(new NetSpeedState { Phase = NetTestPhase.Ping });
        _ = Task.Run(() => RunSpeedTestAsync(_testCts.Token));
    }

    public void CancelSpeedTest()
    {
        try { _testCts?.Cancel(); } catch { }
    }

    private async Task RunSpeedTestAsync(CancellationToken token)
    {
        var state = new NetSpeedState();
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                state.Phase = NetTestPhase.Offline;
                SetTest(state);
                return;
            }

            // ---- ping phase -------------------------------------------------
            state.Phase = NetTestPhase.Ping;
            SetTest(state);
            var samples = new List<double>();
            int failures = 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2.0);
            while (DateTime.UtcNow < deadline && samples.Count < 12 && !token.IsCancellationRequested)
            {
                double? ms = await HttpPingAsync(token).ConfigureAwait(false);
                if (ms is null) failures++; else samples.Add(ms.Value);
                state.ProgressPercent = Math.Min(33, samples.Count / 12.0 * 33);
                state.PingMs = samples.Count > 0 ? Median(samples) : null;
                state.JitterMs = Jitter(samples);
                state.LossPercent = (samples.Count + failures) > 0
                    ? failures / (double)(samples.Count + failures) * 100.0 : null;
                SetTest(state);
            }
            if (samples.Count == 0)
            {
                state.Phase = NetTestPhase.Error;
                state.Error = "No response";
                SetTest(state);
                return;
            }
            token.ThrowIfCancellationRequested();

            // ---- download phase --------------------------------------------
            state.Phase = NetTestPhase.Download;
            state.ProgressPercent = 33;
            SetTest(state);
            double? down = await MeasureDownloadAsync(state, token).ConfigureAwait(false);
            state.DownMbps = down;
            token.ThrowIfCancellationRequested();

            // ---- upload phase ----------------------------------------------
            state.Phase = NetTestPhase.Upload;
            state.ProgressPercent = 66;
            SetTest(state);
            double? up = await MeasureUploadAsync(state, token).ConfigureAwait(false);
            state.UpMbps = up;

            state.Phase = NetTestPhase.Done;
            state.ProgressPercent = 100;
            SetTest(state);
        }
        catch (OperationCanceledException)
        {
            state.Phase = NetTestPhase.Idle;
            state.ProgressPercent = 0;
            SetTest(state);
        }
        catch (Exception ex)
        {
            Log.Info("Speed test failed", ("error", ex.Message));
            state.Phase = NetTestPhase.Error;
            state.Error = ex.Message;
            SetTest(state);
        }
        finally
        {
            Interlocked.Exchange(ref _testRunning, 0);
            Updated?.Invoke();
        }
    }

    private async Task<double?> MeasureDownloadAsync(NetSpeedState state, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(9));
        long loadedSamples = 0; double loadedSum = 0;
        Task? loader = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                double? ms = await HttpPingAsync(cts.Token).ConfigureAwait(false);
                if (ms is not null) { loadedSum += ms.Value; Interlocked.Increment(ref loadedSamples); }
                try { await Task.Delay(1400, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });

        long total = 0, warmBytes = 0;
        double warmTime = 0;
        bool warmed = false;
        var sw = Stopwatch.StartNew();
        long lastReport = 0;
        try
        {
            using var resp = await SharedHttp.Client.GetAsync(
                "https://speed.cloudflare.com/__down?bytes=90000000",
                HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[65536];
            while (true)
            {
                int n;
                try { n = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (n <= 0) break;
                total += n;
                if (!warmed && sw.Elapsed.TotalSeconds >= 1.4)
                {
                    warmed = true; warmBytes = total; warmTime = sw.Elapsed.TotalSeconds;
                }
                if (sw.ElapsedMilliseconds - lastReport > 100)
                {
                    lastReport = sw.ElapsedMilliseconds;
                    double live = LiveMbps(total, warmBytes, warmed ? warmTime : 0, sw.Elapsed.TotalSeconds);
                    state.DownMbps = live;
                    state.ProgressPercent = Math.Min(66, 33 + Math.Min(1.0, sw.Elapsed.TotalSeconds / 9.0) * 33);
                    SetTest(state);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        finally { try { cts.Cancel(); } catch { } try { await loader.ConfigureAwait(false); } catch { } }

        if (Interlocked.Read(ref loadedSamples) > 0)
            state.LoadedMs = loadedSum / Interlocked.Read(ref loadedSamples);

        sw.Stop();
        if (total <= 0) return null;
        double seconds = sw.Elapsed.TotalSeconds;
        if (seconds <= 0.01) return null;
        return LiveMbps(total, warmBytes, warmed ? warmTime : 0, seconds);
    }

    private static double LiveMbps(long total, long warmBytes, double warmTime, double nowSeconds)
    {
        if (nowSeconds > warmTime && warmTime > 0)
        {
            double dt = nowSeconds - warmTime;
            if (dt > 0.05) return Math.Max(0, (total - warmBytes) * 8.0 / dt / 1e6);
        }
        return Math.Max(0, total * 8.0 / Math.Max(0.05, nowSeconds) / 1e6);
    }

    private async Task<double?> MeasureUploadAsync(NetSpeedState state, CancellationToken token)
    {
        const long size = 20_000_000;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(11));
        var sw = Stopwatch.StartNew();
        long sent = 0;
        try
        {
            using var content = new StreamContent(new UploadStream(size, n => sent = n));
            content.Headers.ContentLength = size;
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var lastReport = 0L;
            var reportTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (sw.ElapsedMilliseconds - lastReport > 100)
                    {
                        lastReport = sw.ElapsedMilliseconds;
                        state.UpMbps = sent * 8.0 / Math.Max(0.05, sw.Elapsed.TotalSeconds) / 1e6;
                        state.ProgressPercent = Math.Min(100, 66 + Math.Min(1.0, sent / (double)size) * 34);
                        SetTest(state);
                    }
                    try { await Task.Delay(100, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });
            using var resp = await SharedHttp.Client.PostAsync("https://speed.cloudflare.com/__up", content, cts.Token).ConfigureAwait(false);
            sw.Stop();
            try { cts.Cancel(); } catch { }
            try { await reportTask.ConfigureAwait(false); } catch { }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        sw.Stop();
        long used = Math.Max(sent, 0);
        if (used <= 0) return null;
        double seconds = sw.Elapsed.TotalSeconds;
        if (seconds <= 0.01) return null;
        return used * 8.0 / seconds / 1e6;
    }

    /// <summary>Read-only zero stream that reports how many bytes the HTTP stack has consumed.</summary>
    private sealed class UploadStream : Stream
    {
        private readonly long _length;
        private readonly Action<long> _progress;
        private long _pos;
        public UploadStream(long length, Action<long> progress) { _length = length; _progress = progress; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _pos;
            if (remaining <= 0) return 0;
            int n = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, n);
            _pos += n;
            _progress(_pos);
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            long remaining = _length - _pos;
            if (remaining <= 0) return ValueTask.FromResult(0);
            int n = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..n].Clear();
            _pos += n;
            _progress(_pos);
            return ValueTask.FromResult(n);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private async Task<double?> HttpPingAsync(CancellationToken token)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var resp = await SharedHttp.Client.GetAsync("https://speed.cloudflare.com/__down?bytes=1",
                HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await resp.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private void SetTest(NetSpeedState state)
    {
        lock (_sync) _test = CloneTest(state);
        Updated?.Invoke();
    }

    // ------------------------------------------------------------------ daily totals

    private void LoadDailyTotals()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            // Daily totals live in state.json (runtime data, not a user setting). Older builds kept
            // them in settings.json; read that once as a fallback.
            var st = RuntimeState.Instance.Current;
            string date = st.NetDailyDate;
            long rx = st.NetDailyRxBytes, tx = st.NetDailyTxBytes;
            if (string.IsNullOrEmpty(date))
            {
                var cfg = _settings.Current;
                (date, rx, tx) = (cfg.NetDailyDate, cfg.NetDailyRxBytes, cfg.NetDailyTxBytes);
            }
            if (string.Equals(date, today, StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _dayRx, Math.Max(0, rx));
                Interlocked.Exchange(ref _dayTx, Math.Max(0, tx));
            }
            else
            {
                Interlocked.Exchange(ref _dayRx, 0);
                Interlocked.Exchange(ref _dayTx, 0);
            }
        }
        catch { }
        _dayDate = today;
    }

    /// <summary>"Clear all data": today's transfer totals back to zero (memory and state.json).</summary>
    public void ResetDailyTotals()
    {
        Interlocked.Exchange(ref _dayRx, 0);
        Interlocked.Exchange(ref _dayTx, 0);
        PersistDailyTotals(force: true);
    }

    private void PersistDailyTotals(bool force)
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.Equals(today, _dayDate, StringComparison.Ordinal)) LoadDailyTotals();
        var now = DateTime.UtcNow;
        if (!force && (now - _lastPersistUtc).TotalSeconds < 20) return;
        _lastPersistUtc = now;
        long rx = Interlocked.Read(ref _dayRx);
        long tx = Interlocked.Read(ref _dayTx);
        try
        {
            RuntimeState.Instance.Update(s =>
            {
                s.NetDailyDate = today;
                s.NetDailyRxBytes = rx;
                s.NetDailyTxBytes = tx;
            });
        }
        catch { }
    }

    // ------------------------------------------------------------------ helpers

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        if (n == 0) return 0;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static double? Jitter(List<double> values)
    {
        if (values.Count < 2) return null;
        var sorted = values.ToList();
        double sum = 0;
        for (int i = 1; i < sorted.Count; i++) sum += Math.Abs(sorted[i] - sorted[i - 1]);
        return sum / (sorted.Count - 1);
    }

    private static NetSnapshot CloneSnapshot(NetSnapshot s) => new()
    {
        DownMbps = s.DownMbps,
        UpMbps = s.UpMbps,
        Kind = s.Kind,
        Name = s.Name,
        SignalBars = s.SignalBars,
        RssiDbm = s.RssiDbm,
        LinkMbps = s.LinkMbps,
        Connected = s.Connected,
        Internet = s.Internet,
        TodayRxBytes = s.TodayRxBytes,
        TodayTxBytes = s.TodayTxBytes,
        HasAdapterData = s.HasAdapterData,
    };

    private static NetSpeedState CloneTest(NetSpeedState s) => new()
    {
        Phase = s.Phase,
        ProgressPercent = s.ProgressPercent,
        PingMs = s.PingMs,
        JitterMs = s.JitterMs,
        LossPercent = s.LossPercent,
        DownMbps = s.DownMbps,
        UpMbps = s.UpMbps,
        LoadedMs = s.LoadedMs,
        Error = s.Error,
    };
}
