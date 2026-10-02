using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StemPlayer.Services;

/// <summary>
/// Renders a time-stretched copy of every stem in a track folder via pedalboard's Rubber Band-based
/// time_stretch (pitch-preserving, chosen specifically for minimal artifacts since this is a
/// transcription tool). Offline, not real-time: a full-track render takes tens of seconds even
/// though stems are processed in parallel, so results are cached on disk and reused by factor.
/// </summary>
public class StretchService
{
    readonly PythonEnv _env;
    public StretchService(PythonEnv env) => _env = env;

    static void EnsureScript()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("stretch.py")!;
        using var f = File.Create(Paths.StretchScript);
        s.CopyTo(f);
    }

    /// <summary>Directory holding the stems time-stretched by <paramref name="factor"/> (cached
    /// alongside the originals), or null if rendering failed.</summary>
    public async Task<string?> StretchAsync(string trackDir, double factor, Action<double>? progress = null,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var outDir = Path.Combine(trackDir, $"stretch_{factor.ToString("0.000", CultureInfo.InvariantCulture)}");
        if (Directory.Exists(outDir) && Directory.EnumerateFiles(outDir, "*.wav").Any())
            return outDir;

        EnsureScript();
        string? error = null;
        var args = new[]
        {
            Paths.StretchScript,
            "--dir", trackDir,
            "--outdir", outDir,
            "--factor", factor.ToString(CultureInfo.InvariantCulture),
        };

        int code = await ProcessRunner.RunAsync(_env.Python, args, line =>
        {
            if (!line.StartsWith("@@")) { log?.Invoke(line); return; }
            try
            {
                using var doc = JsonDocument.Parse(line[2..]);
                var r = doc.RootElement;
                if (r.TryGetProperty("progress", out var p)) progress?.Invoke(p.GetDouble());
                if (r.TryGetProperty("error", out var er)) error = er.GetString();
            }
            catch { log?.Invoke(line); }
        }, ct: ct);

        if (code != 0 || error != null)
        {
            log?.Invoke($"Time-stretch failed: {error ?? $"exit code {code}"}");
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
            return null;
        }
        return outDir;
    }
}
