using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StemPlayer.Services;

/// <summary>
/// Detects millisecond-accurate beat timestamps for a track via librosa (already installed as an
/// audio-separator dependency). Internal use only for now — not shown in the UI, just stored on the
/// track for a future feature.
/// </summary>
public class BeatDetectionService
{
    readonly PythonEnv _env;
    public BeatDetectionService(PythonEnv env) => _env = env;

    static void EnsureScript()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("beat_detect.py")!;
        using var f = File.Create(Paths.BeatDetectScript);
        s.CopyTo(f);
    }

    /// <returns>Beat timestamps in milliseconds from the start of <paramref name="audioFile"/>, or an
    /// empty list if detection isn't available or fails (best-effort; never throws except on cancellation).</returns>
    public async Task<List<long>> DetectAsync(string audioFile, Action<string>? log = null, CancellationToken ct = default)
    {
        EnsureScript();
        List<long> beats = new();
        string? error = null;

        int code = await ProcessRunner.RunAsync(_env.Python, new[] { Paths.BeatDetectScript, "--input", audioFile }, line =>
        {
            if (!line.StartsWith("@@")) { log?.Invoke(line); return; }
            try
            {
                using var doc = JsonDocument.Parse(line[2..]);
                var r = doc.RootElement;
                if (r.TryGetProperty("beats_ms", out var b)) beats = b.EnumerateArray().Select(x => x.GetInt64()).ToList();
                if (r.TryGetProperty("error", out var er)) error = er.GetString();
            }
            catch { log?.Invoke(line); }
        }, ct: ct);

        if (code != 0 || error != null)
        {
            log?.Invoke($"Beat detection skipped: {error ?? $"exit code {code}"}");
            return new();
        }
        return beats;
    }
}
