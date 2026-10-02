using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StemPlayer.Services;

public record UpdateInfo(string Tag, Version Version, string AssetUrl, string? Sha256, string? NativeAssetUrl, string? NativeSha256);

/// <summary>Checks the GitHub releases of this project and replaces the installed executable with the newest one.</summary>
public class UpdateService
{
    public const string Repo = "jp-tx/stemplayer";
    public static Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    static readonly HttpClient Http = CreateClient(); // must stay below CurrentVersion (static init order)

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StemPlayer", CurrentVersion.ToString(3)));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>The release asset for this platform, or null when no build is published for it.</summary>
    public static string? AssetName =>
        RuntimeInformation.ProcessArchitecture != Architecture.X64 ? null :
        OperatingSystem.IsWindows() ? "StemPlayer-win-x64.exe" :
        OperatingSystem.IsLinux() ? "StemPlayer-linux-x64" : null;

    /// <summary>
    /// The OpenAL Soft native library ships as a loose file beside the exe (Silk.NET's loader can't find it
    /// inside the single-file bundle). Optional: older releases may not publish this asset.
    /// </summary>
    public static string? NativeAssetName =>
        RuntimeInformation.ProcessArchitecture != Architecture.X64 ? null :
        OperatingSystem.IsWindows() ? "soft_oal.dll" :
        OperatingSystem.IsLinux() ? "libopenal.so" : null;

    /// <summary>Path of the installed single-file executable, or null when running from `dotnet run` / a dll.</summary>
    public static string? InstalledExe
    {
        get
        {
            var p = Environment.ProcessPath;
            if (p == null) return null;
            var name = Path.GetFileNameWithoutExtension(p);
            return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? null : p;
        }
    }

    /// <summary>Removes the previous executable (and native library) left behind by an update (a file in use can be renamed but not deleted).</summary>
    public static void CleanupOldVersion()
    {
        try { if (InstalledExe is { } p && File.Exists(p + ".old")) File.Delete(p + ".old"); } catch { }
        try
        {
            if (InstalledExe is { } p && NativeAssetName != null)
            {
                var native = Path.Combine(Path.GetDirectoryName(p)!, NativeAssetName) + ".old";
                if (File.Exists(native)) File.Delete(native);
            }
        }
        catch { }
    }

    /// <returns>The latest release, or null if it is not newer than <paramref name="current"/>.</returns>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        var asset = AssetName ?? throw new PlatformNotSupportedException("No StemPlayer release is published for this platform.");
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) throw new InvalidOperationException($"Unrecognised release tag '{tag}'.");
        if (Normalize(latest) <= Normalize(current)) return null;

        string? url = null, sha = null, nativeUrl = null, nativeSha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            string? digest = a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:") ? ds["sha256:".Length..] : null;
            if (name == asset) { url = a.GetProperty("browser_download_url").GetString(); sha = digest; }
            else if (name == NativeAssetName) { nativeUrl = a.GetProperty("browser_download_url").GetString(); nativeSha = digest; }
        }
        if (url == null) throw new InvalidOperationException($"Release {tag} has no '{asset}' file.");
        return new UpdateInfo(tag, latest, url, sha, nativeUrl, nativeSha);
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Downloads the release binary, verifies it, and swaps it in for <paramref name="targetExe"/>.
    /// Also refreshes the companion OpenAL native library next to it, best-effort, if the release has one
    /// (older installs that predate it won't have audio until this runs once).</summary>
    public async Task ApplyAsync(UpdateInfo info, string targetExe, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(targetExe)!;
        var tmp = Path.Combine(dir, Path.GetFileName(targetExe) + ".new");
        try
        {
            await DownloadAsync(info.AssetUrl, tmp, info.Sha256, progress, ct);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                          UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            // Linux: rename over the running file is fine. Windows: a running exe can only be renamed, so move it aside first.
            if (OperatingSystem.IsWindows() && File.Exists(targetExe))
                File.Move(targetExe, targetExe + ".old", true);
            File.Move(tmp, targetExe, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }

        if (info.NativeAssetUrl == null || NativeAssetName == null) return;
        var nativeTarget = Path.Combine(dir, NativeAssetName);
        var nativeTmp = nativeTarget + ".new";
        try
        {
            await DownloadAsync(info.NativeAssetUrl, nativeTmp, info.NativeSha256, null, ct);
            if (File.Exists(nativeTarget)) File.Move(nativeTarget, nativeTarget + ".old", true);
            File.Move(nativeTmp, nativeTarget, true);
        }
        catch { /* best-effort: a stale or missing native lib shouldn't fail the whole update */ }
        finally { try { if (File.Exists(nativeTmp)) File.Delete(nativeTmp); } catch { } }
    }

    static async Task DownloadAsync(string url, string destPath, string? sha256, IProgress<double>? progress, CancellationToken ct)
    {
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destPath);
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        if (sha256 != null)
        {
            await using var f = File.OpenRead(destPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Downloaded update failed its SHA-256 check; nothing was changed.");
        }
    }
}
