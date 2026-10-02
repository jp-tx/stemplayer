using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StemPlayer.Services;

public static class ProcessRunner
{
    /// <summary>
    /// Runs a process and streams both stdout and stderr to <paramref name="onLine"/>.
    /// Lines are split on \n and \r so tqdm / yt-dlp progress updates arrive individually.
    /// Returns the exit code.
    /// </summary>
    public static async Task<int> RunAsync(string file, string[] args, Action<string> onLine,
        System.Collections.Generic.IDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception e) { onLine($"Could not start '{file}': {e.Message}"); return -1; }
        p.StandardInput.Close();

        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var t1 = Pump(p.StandardOutput, onLine);
        var t2 = Pump(p.StandardError, onLine);
        await Task.WhenAll(t1, t2);
        await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    static async Task Pump(StreamReader r, Action<string> onLine)
    {
        var sb = new StringBuilder();
        var buf = new char[1024];
        int n;
        while ((n = await r.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                if (buf[i] == '\n' || buf[i] == '\r')
                {
                    if (sb.Length > 0) { onLine(sb.ToString()); sb.Clear(); }
                }
                else sb.Append(buf[i]);
            }
        }
        if (sb.Length > 0) onLine(sb.ToString());
    }
}
