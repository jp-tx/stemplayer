using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using StemPlayer.Models;

namespace StemPlayer.Services;

public enum InstallVariant
{
    Cpu,
    NvidiaCuda,
    /// <summary>Windows only: works on AMD, Intel and NVIDIA GPUs through DirectX 12.</summary>
    DirectML,
}

/// <summary>Finds or creates the Python environment that hosts audio-separator and yt-dlp.</summary>
public class PythonEnv
{
    readonly AppSettings _settings;
    public PythonEnv(AppSettings s) => _settings = s;

    public string Python =>
        !string.IsNullOrWhiteSpace(_settings.PythonPath) ? _settings.PythonPath :
        File.Exists(Paths.VenvPython) ? Paths.VenvPython :
        (OperatingSystem.IsWindows() ? "python" : "python3");

    public bool IsManagedVenvPresent => File.Exists(Paths.VenvPython);

    public async Task<bool> HasModuleAsync(string module)
    {
        int code = await ProcessRunner.RunAsync(Python, new[] { "-c", $"import {module}" }, _ => { });
        return code == 0;
    }

    public Task<bool> HasSeparatorAsync() => HasModuleAsync("audio_separator");
    public Task<bool> HasYtDlpAsync() => HasModuleAsync("yt_dlp");

    public static bool HasFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, exe))) return true;
        return false;
    }

    /// <summary>Creates the managed venv (if needed) and installs audio-separator + yt-dlp.</summary>
    public async Task<bool> InstallAsync(InstallVariant variant, Action<string> log)
    {
        if (!IsManagedVenvPresent)
        {
            var sys = OperatingSystem.IsWindows() ? "python" : "python3";
            log($"Creating virtual environment in {Paths.Venv}");
            int c = await ProcessRunner.RunAsync(sys, new[] { "-m", "venv", Paths.Venv }, log);
            if (c != 0) { log("Failed to create the venv. Is Python 3.10+ installed (with the venv module)?"); return false; }
        }
        var extra = variant switch
        {
            InstallVariant.NvidiaCuda => "audio-separator[gpu]",
            InstallVariant.DirectML => "audio-separator[dml]",
            _ => "audio-separator[cpu]",
        };
        log($"Installing {extra}, yt-dlp, and pedalboard (this can take several minutes)...");
        int code = await ProcessRunner.RunAsync(Paths.VenvPython,
            new[] { "-m", "pip", "install", "--upgrade", extra, "yt-dlp", "audioread", "pedalboard" }, log);
        log(code == 0 ? "Install finished." : $"pip exited with code {code}.");
        if (!HasFfmpeg()) log("WARNING: ffmpeg was not found on PATH. It is required for both separation and YouTube import.");
        return code == 0;
    }
}
