using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StemPlayer.Models;

namespace StemPlayer.Services;

/// <summary>Runs python-audio-separator on a file and returns the stems it produced.</summary>
public class SeparatorService
{
    static readonly Regex Pct = new(@"(\d{1,3})%\|", RegexOptions.Compiled);
    static readonly Regex Role = new(@"\(([^)]+)\)", RegexOptions.Compiled);
    readonly PythonEnv _env;
    readonly AppSettings _settings;

    public SeparatorService(PythonEnv env, AppSettings settings) { _env = env; _settings = settings; }

    static int ExpectedPasses(string model) =>
        model.StartsWith("htdemucs_ft") ? 8 : model.StartsWith("htdemucs") ? 2 : 1;

    static void EnsureRunner()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("runner.py")!;
        using var f = File.Create(Paths.Runner);
        s.CopyTo(f);
    }

    /// <returns>role (vocals, bass, ...) to file name, relative to <paramref name="trackDir"/>.</returns>
    public async Task<Dictionary<string, string>> SeparateAsync(string inputFile, string trackDir,
        Action<double> progress, Action<string> stage, Action<string> log, CancellationToken ct)
    {
        EnsureRunner();
        var tmp = Path.Combine(trackDir, "_tmp");
        Directory.CreateDirectory(tmp);

        var args = new[]
        {
            Paths.Runner,
            "--input", inputFile,
            "--outdir", tmp,
            "--model", _settings.Model,
            "--models-dir", Paths.Models,
            "--device", _settings.Device == DeviceMode.Cpu ? "cpu" : "gpu",
        };

        // CUDA / ROCm honour these; the runner additionally forces CPU in torch + onnxruntime.
        var env = new Dictionary<string, string>();
        if (_settings.Device == DeviceMode.Cpu)
        {
            env["CUDA_VISIBLE_DEVICES"] = "-1";
            env["HIP_VISIBLE_DEVICES"] = "-1";
        }

        bool separating = false;
        // Demucs runs `shifts` (2) passes per bag model; tqdm restarts at 0% for each pass.
        int expectedPasses = ExpectedPasses(_settings.Model), pass = 0, lastPct = 0;
        string? error = null;
        List<string> files = new();

        int code = await ProcessRunner.RunAsync(_env.Python, args, line =>
        {
            if (line.StartsWith("@@"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line[2..]);
                    var r = doc.RootElement;
                    if (r.TryGetProperty("stage", out var st))
                    {
                        separating = st.GetString() == "separating";
                        stage(separating ? "Separating" : "Loading model");
                        progress(0); pass = 0; lastPct = 0;
                    }
                    if (r.TryGetProperty("files", out var fs)) files = fs.EnumerateArray().Select(x => x.GetString()!).ToList();
                    if (r.TryGetProperty("error", out var er)) error = er.GetString();
                    if (r.TryGetProperty("warn", out var w)) log(w.GetString() ?? "");
                }
                catch { log(line); }
                return;
            }
            var m = Pct.Match(line);
            if (m.Success && separating)
            {
                int pct = Math.Min(100, int.Parse(m.Groups[1].Value));
                if (pct < lastPct) pass++;
                lastPct = pct;
                progress(Math.Min(0.99, (pass + pct / 100.0) / expectedPasses));
            }
            else log(line);
        }, env, ct);

        if (code != 0 || files.Count == 0)
            throw new InvalidOperationException(error ?? $"audio-separator exited with code {code}. Check Options > Set up Python environment.");

        var result = new Dictionary<string, string>();
        foreach (var f in files)
        {
            var full = Path.IsPathRooted(f) ? f : Path.Combine(tmp, f);
            var m = Role.Match(Path.GetFileName(full));
            var role = m.Success ? m.Groups[1].Value.ToLowerInvariant().Replace(' ', '_') : "other";
            if (role is "no_vocals" or "inst") role = "instrumental";
            var dest = role + ".wav";
            File.Move(full, Path.Combine(trackDir, dest), true);
            result[role] = dest;
        }
        try { Directory.Delete(tmp, true); } catch { }
        return result;
    }
}
