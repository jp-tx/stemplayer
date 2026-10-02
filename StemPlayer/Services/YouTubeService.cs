using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StemPlayer.Services;

public record YouTubeResult(string Mp3Path, string Title, string Artist, double Duration);

/// <summary>Downloads a YouTube URL as an mp3 using yt-dlp (run from the managed Python env).</summary>
public class YouTubeService
{
    static readonly Regex Pct = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
    readonly PythonEnv _env;
    public YouTubeService(PythonEnv env) => _env = env;

    public async Task<YouTubeResult> DownloadAsync(string url, string outDir, Action<double> progress, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        string? file = null, title = null, artist = null;
        double duration = 0;

        var args = new[]
        {
            "-m", "yt_dlp",
            "--no-playlist", "--no-simulate", "--newline", "--progress",
            "-x", "--audio-format", "mp3", "--audio-quality", "0",
            "-o", Path.Combine(outDir, "source.%(ext)s"),
            "--print", "before_dl:@@META\t%(title)s\t%(artist,uploader,channel)s\t%(duration)s",
            "--print", "after_move:@@FILE\t%(filepath)s",
            url,
        };

        int code = await ProcessRunner.RunAsync(_env.Python, args, line =>
        {
            if (line.StartsWith("@@META\t"))
            {
                var p = line.Split('\t');
                if (p.Length >= 4)
                {
                    title = p[1]; artist = p[2];
                    double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                }
            }
            else if (line.StartsWith("@@FILE\t")) file = line[7..].Trim();
            else
            {
                var m = Pct.Match(line);
                if (m.Success) progress(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0);
                else log(line);
            }
        }, ct: ct);

        if (code != 0) throw new InvalidOperationException(
            "yt-dlp failed. Make sure yt-dlp and ffmpeg are installed (Options > Set up Python environment). Last output is in the log.");
        file ??= Path.Combine(outDir, "source.mp3");
        if (!File.Exists(file)) throw new FileNotFoundException("yt-dlp did not produce an mp3", file);
        return new YouTubeResult(file, title ?? "YouTube import", artist ?? "", duration);
    }
}
