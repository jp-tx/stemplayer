using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StemPlayer.Models;

namespace StemPlayer.Services;

/// <summary>Beat grid + meter for a track. Internal use only for now — not shown in the UI beyond the
/// tempo/time-signature summary. <see cref="BeatsPerBar"/> is null if detection isn't available or
/// failed (distinct from "4", which is the confident-or-default result).</summary>
public record BeatInfo(List<long> BeatsMs, double Tempo, int? BeatsPerBar, double MeterConfidence, List<BeatSegment> Segments);

/// <summary>
/// Detects millisecond-accurate beat timestamps, a 3-vs-4 beats-per-bar estimate, and beat-stable
/// segments (so a mid-song stop doesn't force one bad global downbeat phase) via librosa (already
/// installed as an audio-separator dependency).
/// </summary>
public class BeatDetectionService
{
    static readonly BeatInfo Empty = new(new(), 0, null, 0, new());

    readonly PythonEnv _env;
    public BeatDetectionService(PythonEnv env) => _env = env;

    static void EnsureScript()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("beat_detect.py")!;
        using var f = File.Create(Paths.BeatDetectScript);
        s.CopyTo(f);
    }

    /// <param name="audioFile">Full mix, used for tempo + beat timing.</param>
    /// <param name="accentFile">Optional isolated drums stem, used for meter grouping (falls back to
    /// <paramref name="audioFile"/> when not separated for this track's model).</param>
    public async Task<BeatInfo> DetectAsync(string audioFile, string? accentFile = null, Action<string>? log = null, CancellationToken ct = default)
    {
        EnsureScript();
        var args = accentFile == null
            ? new[] { Paths.BeatDetectScript, "--input", audioFile }
            : new[] { Paths.BeatDetectScript, "--input", audioFile, "--accent", accentFile };

        List<long> beats = new();
        double tempo = 0, confidence = 0;
        int? beatsPerBar = null;
        List<BeatSegment> segments = new();
        string? error = null;

        int code = await ProcessRunner.RunAsync(_env.Python, args, line =>
        {
            if (!line.StartsWith("@@")) { log?.Invoke(line); return; }
            try
            {
                using var doc = JsonDocument.Parse(line[2..]);
                var r = doc.RootElement;
                if (r.TryGetProperty("beats_ms", out var b)) beats = b.EnumerateArray().Select(x => x.GetInt64()).ToList();
                if (r.TryGetProperty("tempo", out var t)) tempo = t.GetDouble();
                if (r.TryGetProperty("beats_per_bar", out var bpb)) beatsPerBar = bpb.GetInt32();
                if (r.TryGetProperty("meter_confidence", out var mc)) confidence = mc.GetDouble();
                if (r.TryGetProperty("segments", out var segs))
                    segments = segs.EnumerateArray().Select(s => new BeatSegment
                    {
                        StartBeatIndex = s.GetProperty("start").GetInt32(),
                        EndBeatIndex = s.GetProperty("end").GetInt32(),
                        DownbeatOffset = s.GetProperty("downbeat_offset").GetInt32(),
                    }).ToList();
                if (r.TryGetProperty("error", out var er)) error = er.GetString();
            }
            catch { log?.Invoke(line); }
        }, ct: ct);

        if (code != 0 || error != null)
        {
            log?.Invoke($"Beat detection skipped: {error ?? $"exit code {code}"}");
            return Empty;
        }
        return new BeatInfo(beats, tempo, beatsPerBar, confidence, segments);
    }
}
