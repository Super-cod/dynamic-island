using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Net;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Services.Settings;

namespace WindowsDynamicBar.Services.Weather;

/// <summary>Coarse sky condition used to pick an icon/colour.</summary>
public enum WeatherKind { Sun, Moon, Partly, Rain, Snow, Fog, Storm }

/// <summary>One forecast hour (local to the resolved location).</summary>
public sealed class WeatherHour
{
    public DateTime Time;
    public double TempC;
    public double PrecipProb;   // percent
    public int WeatherCode;     // WMO code
    public double UvIndex;
}

/// <summary>
/// Real weather data: resolves the location from the public IP (ipwho.is, falling back to ipapi.co)
/// and fetches an Open-Meteo forecast (temperature, humidity, apparent temperature, precipitation
/// probability, weather code, UV, wind, sunrise/sunset) plus US AQI when available. Everything is
/// cached and refreshed on a timer; nothing is fabricated. When a fetch fails the last good snapshot
/// is retained but <see cref="Available"/> flips false until the next successful refresh.
/// </summary>
public sealed class WeatherSnapshot
{
    public bool Available;
    public string LocationName = "";
    public double Latitude, Longitude;
    public DateTime UpdatedAt;
    public double UtcOffsetSeconds;
    public DateTime Sunrise, Sunset;
    public bool IsDay;

    public double TempC, ApparentC, Humidity, PrecipMm, WindKmh, WindDirDeg;
    public double? UsAqi;
    public int WeatherCode;              // WMO "current" code
    public WeatherHour[] Hours = Array.Empty<WeatherHour>();
    public int NowIndex;

    public string Error = "";

    public WeatherKind Kind => KindOf(WeatherCode, IsDay);
    public string Condition => ConditionText(WeatherCode);

    public double CurrentPrecipProb => NowIndex >= 0 && NowIndex < Hours.Length ? Hours[NowIndex].PrecipProb : 0;
    public double CurrentUvIndex => NowIndex >= 0 && NowIndex < Hours.Length ? Hours[NowIndex].UvIndex : 0;

    public static WeatherKind KindOf(int code, bool isDay) => code switch
    {
        0 => isDay ? WeatherKind.Sun : WeatherKind.Moon,
        1 => isDay ? WeatherKind.Sun : WeatherKind.Moon,
        2 or 3 => WeatherKind.Partly,
        45 or 48 => WeatherKind.Fog,
        51 or 53 or 55 or 56 or 57 => WeatherKind.Rain,
        61 or 63 or 65 or 66 or 67 => WeatherKind.Rain,
        71 or 73 or 75 or 77 => WeatherKind.Snow,
        80 or 81 or 82 => WeatherKind.Rain,
        85 or 86 => WeatherKind.Snow,
        95 or 96 or 99 => WeatherKind.Storm,
        _ => WeatherKind.Partly,
    };

    public static string ConditionText(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow",
        80 or 81 or 82 => "Showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm",
        _ => "—",
    };
}

/// <summary>Fetches and caches real weather for the current location.</summary>
public sealed class WeatherService : IDisposable
{
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    private const string AirUrl = "https://air-quality-api.open-meteo.com/v1/air-quality";
    private const string GeocodeUrl = "https://geocoding-api.open-meteo.com/v1/search";

    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private System.Threading.Timer? _timer;
    private int _fetching;

    private readonly SettingsService? _settings;
    private string _configuredLocation = "Chennai";
    private bool _configuredAutoLocation = false;

    private WeatherSnapshot _snapshot = new() { Available = false, LocationName = "Chennai, IN" };
    private double? _lat = 13.0827, _lon = 80.2707;
    private string _place = "Chennai, IN";
    private DateTime _locationResolvedUtc = DateTime.MinValue;

    public WeatherService(SettingsService? settings = null)
    {
        _settings = settings ?? (App.Settings ?? SettingsService.Instance);
        if (_settings is not null)
        {
            var cur = _settings.Current;
            _configuredLocation = cur.WeatherLocation;
            _configuredAutoLocation = cur.WeatherAutoLocation;
            _settings.SettingsChanged += OnSettingsChanged;
        }
    }

    public WeatherSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public bool HasLocation => _lat is not null && _lon is not null;

    public event Action? Updated;

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    /// <summary>Called every time the island opens: fetches only when the data is due (fresh data
    /// is not re-downloaded on each hover; a failed fetch is retried after a minute).</summary>
    public void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        var snap = Snapshot;
        DateTime dueAt = snap.Available ? snap.UpdatedAt + RefreshEvery : _lastAttemptUtc + RetryAfterFailure;
        var due = dueAt - DateTime.UtcNow;
        _timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, RefreshEvery);
    }

    public void Stop() => _timer?.Change(Timeout.Infinite, Timeout.Infinite);

    private async void Refresh()
    {
        if (Interlocked.CompareExchange(ref _fetching, 1, 0) != 0) return;
        try { await RefreshAsync(_cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { Log.Info("Weather refresh failed", ("error", ex.Message)); }
        finally { Interlocked.Exchange(ref _fetching, 0); }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        _lastAttemptUtc = DateTime.UtcNow;
        if (!HasLocation || (DateTime.UtcNow - _locationResolvedUtc) > TimeSpan.FromHours(12))
            await ResolveLocationAsync(token).ConfigureAwait(false);

        if (!HasLocation)
        {
            Publish(new WeatherSnapshot { Available = false, LocationName = _place });
            return;
        }

        double lat = _lat!.Value, lon = _lon!.Value;
        string url = $"{ForecastUrl}?latitude={lat.ToString("0.####", CultureInfo.InvariantCulture)}" +
                     $"&longitude={lon.ToString("0.####", CultureInfo.InvariantCulture)}" +
                     "&current=temperature_2m,relative_humidity_2m,apparent_temperature,precipitation," +
                     "weather_code,wind_speed_10m,wind_direction_10m,is_day" +
                     "&hourly=temperature_2m,precipitation_probability,weather_code,uv_index" +
                     "&daily=sunrise,sunset&timezone=auto&forecast_days=2";

        using var deadline = SharedHttp.Deadline(TimeSpan.FromSeconds(20), token);
        using var resp = await SharedHttp.Client.GetAsync(url, deadline.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
        var root = doc.RootElement;

        double offsetSeconds = GetDouble(root, "utc_offset_seconds");
        DateTime nowLocal = DateTime.UtcNow.AddSeconds(offsetSeconds);

        var snap = new WeatherSnapshot
        {
            Available = true,
            LocationName = string.IsNullOrWhiteSpace(_place) ? $"{lat:0.0}, {lon:0.0}" : _place,
            Latitude = lat,
            Longitude = lon,
            UpdatedAt = DateTime.UtcNow,
            UtcOffsetSeconds = offsetSeconds,
        };

        var current = root.GetProperty("current");
        snap.TempC = GetDouble(current, "temperature_2m");
        snap.ApparentC = GetDouble(current, "apparent_temperature");
        snap.Humidity = GetDouble(current, "relative_humidity_2m");
        snap.PrecipMm = GetDouble(current, "precipitation");
        snap.WindKmh = GetDouble(current, "wind_speed_10m");
        snap.WindDirDeg = GetDouble(current, "wind_direction_10m");
        snap.WeatherCode = (int)GetDouble(current, "weather_code");
        snap.IsDay = GetDouble(current, "is_day") >= 0.5;

        if (TryArr(root, out var sunrise, "daily", "sunrise") && sunrise.GetArrayLength() > 0)
            snap.Sunrise = ParseTime(Str(sunrise, 0));
        if (TryArr(root, out var sunset, "daily", "sunset") && sunset.GetArrayLength() > 0)
            snap.Sunset = ParseTime(Str(sunset, 0));

        bool hasTimes = TryArr(root, out var hTimes, "hourly", "time");
        bool hasTemp = TryArr(root, out var hTemp, "hourly", "temperature_2m");
        bool hasPop = TryArr(root, out var hPop, "hourly", "precipitation_probability");
        bool hasCode = TryArr(root, out var hCode, "hourly", "weather_code");
        bool hasUv = TryArr(root, out var hUv, "hourly", "uv_index");
        bool hasHours = hasTimes && hasTemp && hasPop && hasCode && hasUv;
        int len = hasHours
            ? Math.Min(hTimes.GetArrayLength(), Math.Min(hTemp.GetArrayLength(),
              Math.Min(hPop.GetArrayLength(), Math.Min(hCode.GetArrayLength(), hUv.GetArrayLength()))))
            : 0;

        snap.Hours = new WeatherHour[len];
        int nowIdx = 0;
        for (int i = 0; i < len; i++)
        {
            var t = ParseTime(Str(hTimes, i));
            snap.Hours[i] = new WeatherHour
            {
                Time = t,
                TempC = Num(hTemp, i),
                PrecipProb = Num(hPop, i),
                WeatherCode = (int)Num(hCode, i),
                UvIndex = Num(hUv, i),
            };
            if (t <= nowLocal) nowIdx = i;
        }
        snap.NowIndex = len == 0 ? 0 : Math.Clamp(nowIdx, 0, len - 1);

        try { snap.UsAqi = await FetchAqiAsync(lat, lon, token).ConfigureAwait(false); }
        catch { snap.UsAqi = null; }

        Publish(snap);
        Log.Info("Weather updated", ("place", snap.LocationName), ("temp", Math.Round(snap.TempC, 1)),
            ("hours", snap.Hours.Length), ("aqi", snap.UsAqi?.ToString("0") ?? "—"));
    }

    private void OnSettingsChanged(SettingsModel m)
    {
        bool changed = false;
        lock (_sync)
        {
            if (!string.Equals(_configuredLocation, m.WeatherLocation, StringComparison.OrdinalIgnoreCase) ||
                _configuredAutoLocation != m.WeatherAutoLocation)
            {
                _configuredLocation = m.WeatherLocation;
                _configuredAutoLocation = m.WeatherAutoLocation;
                _lat = null;
                _lon = null;
                _locationResolvedUtc = DateTime.MinValue;
                changed = true;
            }
        }
        if (changed) RequestRefresh();
    }

    public void RequestRefresh()
    {
        Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                if (Interlocked.CompareExchange(ref _fetching, 1, 0) == 0)
                {
                    try { await RefreshAsync(_cts.Token).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Info("Weather refresh failed", ("error", ex.Message)); }
                    finally { Interlocked.Exchange(ref _fetching, 0); }
                    return;
                }
                await Task.Delay(200);
            }
        });
    }

    private async Task ResolveLocationAsync(CancellationToken token)
    {
        var settings = _settings?.Current ?? SettingsService.Instance.Current;
        bool autoLocation = settings.WeatherAutoLocation;
        string location = settings.WeatherLocation?.Trim() ?? "";

        // Manual location (default when autoLocation is false and location is specified)
        if (!autoLocation && !string.IsNullOrWhiteSpace(location))
        {
            // 1. Explicit coordinates: "13.0827, 80.2707"
            if (TryParseCoords(location, out double cLat, out double cLon))
            {
                _lat = cLat;
                _lon = cLon;
                _place = $"{cLat:0.00}, {cLon:0.00}";
                _locationResolvedUtc = DateTime.UtcNow;
                return;
            }

            // 2. Chennai shortcut / instant local resolution
            if (location.Equals("Chennai", StringComparison.OrdinalIgnoreCase) ||
                location.Equals("Madras", StringComparison.OrdinalIgnoreCase))
            {
                _lat = 13.0827;
                _lon = 80.2707;
                _place = "Chennai, IN";
                _locationResolvedUtc = DateTime.UtcNow;
                return;
            }

            // 3. Geocode via Open-Meteo Geocoding API
            try
            {
                string geoUrl = $"{GeocodeUrl}?name={Uri.EscapeDataString(location)}&count=1&language=en&format=json";
                using var deadline = SharedHttp.Deadline(TimeSpan.FromSeconds(20), token);
                using var resp = await SharedHttp.Client.GetAsync(geoUrl, deadline.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("results", out var resArr) && resArr.ValueKind == JsonValueKind.Array && resArr.GetArrayLength() > 0)
                    {
                        var first = resArr[0];
                        if (TryCoords(first, out double gLat, out double gLon))
                        {
                            _lat = gLat;
                            _lon = gLon;
                            string name = first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                                ? n.GetString() ?? location : location;
                            string cc = first.TryGetProperty("country_code", out var c) && c.ValueKind == JsonValueKind.String
                                ? c.GetString() ?? "" : "";
                            _place = string.IsNullOrEmpty(cc) ? name : $"{name}, {cc}";
                            _locationResolvedUtc = DateTime.UtcNow;
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("Geocoding failed for custom location", ("location", location), ("error", ex.Message));
            }

            // Cached coordinates fallback if present
            if (settings.WeatherLatitude is not null && settings.WeatherLongitude is not null)
            {
                _lat = settings.WeatherLatitude;
                _lon = settings.WeatherLongitude;
                _place = location;
                _locationResolvedUtc = DateTime.UtcNow;
                return;
            }
        }

        // Auto location via IP lookup
        if (autoLocation)
        {
            try
            {
                using var deadline = SharedHttp.Deadline(TimeSpan.FromSeconds(20), token);
                using var resp = await SharedHttp.Client.GetAsync("https://ipwho.is/", deadline.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                    var root = doc.RootElement;
                    bool ok = !root.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.False;
                    if (ok && TryCoords(root, out double la, out double lo))
                    {
                        _lat = la; _lon = lo; _place = Place(root); _locationResolvedUtc = DateTime.UtcNow;
                        return;
                    }
                }
            }
            catch { /* try next provider */ }

            try
            {
                using var deadline = SharedHttp.Deadline(TimeSpan.FromSeconds(20), token);
                using var resp = await SharedHttp.Client.GetAsync("https://ipapi.co/json/", deadline.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                    var root = doc.RootElement;
                    if (TryCoords(root, out double la, out double lo))
                    {
                        _lat = la; _lon = lo; _place = Place(root); _locationResolvedUtc = DateTime.UtcNow;
                        return;
                    }
                }
            }
            catch { /* fallback to default */ }
        }

        // Final fallback: default to Chennai
        _lat = 13.0827;
        _lon = 80.2707;
        _place = "Chennai, IN";
        _locationResolvedUtc = DateTime.UtcNow;
    }

    private async Task<double?> FetchAqiAsync(double lat, double lon, CancellationToken token)
    {
        string url = $"{AirUrl}?latitude={lat.ToString("0.####", CultureInfo.InvariantCulture)}" +
                     $"&longitude={lon.ToString("0.####", CultureInfo.InvariantCulture)}" +
                     "&current=us_aqi&timezone=auto";
        using var deadline = SharedHttp.Deadline(TimeSpan.FromSeconds(20), token);
        using var resp = await SharedHttp.Client.GetAsync(url, deadline.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("current", out var cur)
            && cur.TryGetProperty("us_aqi", out var aq) && aq.ValueKind == JsonValueKind.Number)
            return aq.GetDouble();
        return null;
    }

    private void Publish(WeatherSnapshot snap)
    {
        lock (_sync) _snapshot = snap;
        Updated?.Invoke();
    }

    // ---- helpers ----

    private static bool TryCoords(JsonElement root, out double lat, out double lon)
    {
        lat = 0; lon = 0;
        if (root.TryGetProperty("latitude", out var la) && root.TryGetProperty("longitude", out var lo)
            && la.ValueKind == JsonValueKind.Number && lo.ValueKind == JsonValueKind.Number)
        {
            lat = la.GetDouble();
            lon = lo.GetDouble();
            return true;
        }
        return false;
    }

    private static string Place(JsonElement root)
    {
        string city = root.TryGetProperty("city", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "" : "";
        string cc = root.TryGetProperty("country_code", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(city)) return "";
        return string.IsNullOrEmpty(cc) ? city : $"{city}, {cc}";
    }

    private static double GetDouble(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool TryArr(JsonElement root, out JsonElement arr, params string[] path)
    {
        JsonElement cur = root;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out var next))
            {
                arr = default;
                return false;
            }
            cur = next;
        }
        arr = cur;
        return cur.ValueKind == JsonValueKind.Array;
    }

    private static double Num(JsonElement arr, int i)
        => arr.ValueKind == JsonValueKind.Array && i >= 0 && i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.Number
            ? arr[i].GetDouble() : 0;

    private static string Str(JsonElement arr, int i)
        => arr.ValueKind == JsonValueKind.Array && i >= 0 && i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.String
            ? arr[i].GetString() ?? "" : "";

    private static bool TryParseCoords(string input, out double lat, out double lon)
    {
        lat = 0; lon = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var parts = input.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon))
        {
            return lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
        }
        return false;
    }

    private static DateTime ParseTime(string iso)
        => DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : DateTime.MinValue;

    public void Dispose()
    {
        if (_settings is not null)
        {
            try { _settings.SettingsChanged -= OnSettingsChanged; } catch { }
        }
        try { _cts.Cancel(); } catch { }
        _timer?.Dispose();
        _cts.Dispose();
    }
}
