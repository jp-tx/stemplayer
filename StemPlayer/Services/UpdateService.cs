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

public record UpdateInfo(string Tag, Version Version, string AssetUrl, string? Sha256);

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

    /// <summary>Removes the previous executable left behind by a Windows update (a running exe can be renamed but not deleted).</summary>
    public static void CleanupOldVersion()
    {
        try { if (InstalledExe is { } p && File.Exists(p + ".old")) File.Delete(p + ".old"); } catch { }
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

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() != asset) continue;
            string? sha = null;
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:"))
                sha = ds["sha256:".Length..];
            return new UpdateInfo(tag, latest, a.GetProperty("browser_download_url").GetString()!, sha);
        }
        throw new InvalidOperationException($"Release {tag} has no '{asset}' file.");
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Downloads the release binary, verifies it, and swaps it in for <paramref name="targetExe"/>.</summary>
    public async Task ApplyAsync(UpdateInfo info, string targetExe, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(targetExe)!;
        var tmp = Path.Combine(dir, Path.GetFileName(targetExe) + ".new");
        try
        {
            using (var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                var buf = new byte[81920];
                long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            if (info.Sha256 != null)
            {
                await using var f = File.OpenRead(tmp);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
                if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Downloaded update failed its SHA-256 check; nothing was changed.");
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                          UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            // Linux: rename over the running file is fine. Windows: a running exe can only be renamed, so move it aside first.
            if (OperatingSystem.IsWindows() && File.Exists(targetExe))
                File.Move(targetExe, targetExe + ".old", true);
            File.Move(tmp, targetExe, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }
}
