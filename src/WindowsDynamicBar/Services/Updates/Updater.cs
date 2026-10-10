using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsDynamicBar.Core.Events;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Net;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.Services.Updates;

/// <summary>One release in the update feed (JSON published next to the installer).</summary>
public sealed record UpdateManifest(string Version, string Url, string Sha256, string? Notes);

/// <summary>
/// Update check for the Inno Setup build. The feed address is a setting (UpdateFeedUrl) filled in
/// by whoever publishes releases - no address is built in. The feed is a small JSON file:
/// { "version": "1.2.0", "url": "https://.../WindowsDynamicBar-1.2.0-setup.exe", "sha256": "...", "notes": "..." }.
/// A newer version is downloaded to %TEMP%, its SHA-256 is verified, and a card says it is ready;
/// installing (per-user, silent, then restart) only happens when the user confirms.
/// </summary>
public static class Updater
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string? ReadyInstaller { get; private set; }
    public static string? ReadyVersion { get; private set; }

    public static async Task<string> CheckAsync(SettingsModel s, EventBus bus, bool interactive)
    {
        if (string.IsNullOrWhiteSpace(s.UpdateFeedUrl)) return "No update address is set (Settings > About).";
        if (!Uri.TryCreate(s.UpdateFeedUrl, UriKind.Absolute, out var feed) || feed.Scheme != Uri.UriSchemeHttps)
            return "The update address must be an https:// link.";
        if (!await Gate.WaitAsync(0)) return "Already checking…";
        try
        {
            using var cts = SharedHttp.Deadline(TimeSpan.FromSeconds(15));
            var manifest = await SharedHttp.Client.GetFromJsonSafeAsync(feed, cts.Token);
            if (manifest is null) return "The update feed could not be read.";
            if (!SemVer.TryParse(manifest.Version, out var remote) || !SemVer.TryParse(App.InfoVersion, out var local))
                return "The update feed has an invalid version.";
            if (SemVer.Compare(remote, local) <= 0) return $"You have the latest version ({App.InfoVersion}).";

            string file = await DownloadAsync(manifest);
            ReadyInstaller = file;
            ReadyVersion = manifest.Version;
            bus.Raise(BarEvent.Create(BarEventKind.Toast, "Update ready", $"Version {manifest.Version} · install from the tray menu",
                priority: 80, displayDuration: TimeSpan.FromSeconds(8)));
            return $"Version {manifest.Version} is downloaded and ready to install.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidDataException)
        {
            Log.Warning("Update check failed", ("error", ex.Message));
            return interactive ? $"Update check failed: {ex.Message}" : "";
        }
        finally { Gate.Release(); }
    }

    private static async Task<string> DownloadAsync(UpdateManifest m)
    {
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Installer link must be https://");
        string dir = Path.Combine(Path.GetTempPath(), "WindowsDynamicBar-update");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"WindowsDynamicBar-{m.Version}-setup.exe");
        using var cts = SharedHttp.Deadline(TimeSpan.FromMinutes(10));
        using (var resp = await SharedHttp.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(file);
            await resp.Content.CopyToAsync(fs, cts.Token);
        }
        await using (var check = File.OpenRead(file))
        {
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cts.Token));
            if (!hash.Equals(m.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                throw new InvalidDataException("Downloaded installer does not match the published checksum");
            }
        }
        return file;
    }

    /// <summary>Run the verified installer (per-user, no admin) and let it restart the app.</summary>
    public static bool InstallReady()
    {
        if (ReadyInstaller is null || !File.Exists(ReadyInstaller)) return false;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReadyInstaller,
            "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true });
        return true;
    }

    private static async Task<UpdateManifest?> GetFromJsonSafeAsync(this HttpClient http, Uri feed, CancellationToken ct)
    {
        using var resp = await http.GetAsync(feed, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is > 64 * 1024) throw new InvalidDataException("Update feed is too large");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<UpdateManifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
    }
}

/// <summary>Minimal SemVer (major.minor.patch, optional -prerelease which sorts lower).</summary>
public static class SemVer
{
    public static bool TryParse(string? text, out (int Major, int Minor, int Patch, string Pre) v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().TrimStart('v', 'V');
        string pre = "";
        int dash = t.IndexOf('-');
        if (dash >= 0) { pre = t[(dash + 1)..]; t = t[..dash]; }
        int plus = pre.IndexOf('+');
        if (plus >= 0) pre = pre[..plus];
        var parts = t.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        int[] n = new int[3];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out n[i]) || n[i] < 0) return false;
        v = (n[0], n[1], n[2], pre);
        return true;
    }

    public static int Compare((int Major, int Minor, int Patch, string Pre) a, (int Major, int Minor, int Patch, string Pre) b)
    {
        int c = a.Major.CompareTo(b.Major);
        if (c == 0) c = a.Minor.CompareTo(b.Minor);
        if (c == 0) c = a.Patch.CompareTo(b.Patch);
        if (c != 0) return c;
        if (a.Pre.Length == 0 && b.Pre.Length == 0) return 0;
        if (a.Pre.Length == 0) return 1;    // 1.2.0 > 1.2.0-beta
        if (b.Pre.Length == 0) return -1;
        return string.CompareOrdinal(a.Pre, b.Pre);
    }
}
