using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        IsManagedVenvPresent ? Paths.VenvPython :
        FindSystemPython() ?? (OperatingSystem.IsWindows() ? "python" : "python3");

    static string? _systemPython;

    /// <summary>
    /// Locates a working Python 3.10+ by scanning PATH (skipping the Windows Store stub), then the
    /// py launcher and the usual install folders. Returns null if none is found.
    /// </summary>
    public static string? FindSystemPython(bool refresh = false)
    {
        if (!refresh && _systemPython != null && File.Exists(_systemPython)) return _systemPython;
        _systemPython = null;
        foreach (var c in Candidates())
            if (WorksAsPython(c)) return _systemPython = c;
        return null;
    }

    static IEnumerable<string> Candidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = OperatingSystem.IsWindows() ? new[] { "python.exe" } : new[] { "python3", "python" };
        var dirs = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            // Registry PATH can be newer than this process's copy (e.g. right after a winget install).
            foreach (var t in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                dirs.AddRange((Environment.GetEnvironmentVariable("PATH", t) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var root in new[] { Path.Combine(local, "Programs", "Python"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
                if (Directory.Exists(root))
                    foreach (var d in Directory.GetDirectories(root, "Python3*").OrderByDescending(x => x))
                        dirs.Add(d);
            dirs.Add(Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? "", "miniconda3"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniconda3"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "anaconda3"));
        }
        else
        {
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries));
            dirs.AddRange(new[] { "/usr/local/bin", "/usr/bin", "/opt/homebrew/bin" });
        }
        foreach (var d in dirs)
            foreach (var n in names)
            {
                string f;
                try { f = Path.Combine(d.Trim('"'), n); } catch { continue; }
                // The Store "python.exe" in WindowsApps is a stub that opens the Store instead of running Python.
                if (f.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(f) && seen.Add(f)) yield return f;
            }
    }

    static bool WorksAsPython(string exe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)");
            using var p = System.Diagnostics.Process.Start(psi)!;
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Finds Python, installing it (winget on Windows, Homebrew on macOS) if it is missing.</summary>
    public static async Task<string?> EnsureSystemPythonAsync(Action<string> log)
    {
        var py = FindSystemPython(refresh: true);
        if (py != null) return py;

        log("Python 3.10+ was not found on PATH. Attempting to install it...");
        if (OperatingSystem.IsWindows())
            await ProcessRunner.RunAsync("winget", new[] { "install", "--id", "Python.Python.3.12", "-e", "--silent",
                "--accept-package-agreements", "--accept-source-agreements" }, log);
        else if (OperatingSystem.IsMacOS())
            await ProcessRunner.RunAsync("brew", new[] { "install", "python" }, log);
        else
        {
            log("Automatic install isn't supported here. Install python3 and python3-venv with your package manager (e.g. sudo apt install python3 python3-venv).");
            return null;
        }

        py = FindSystemPython(refresh: true);
        log(py != null ? $"Using Python at {py}" : "Python still could not be found after installing. Install Python 3.10+ manually or set its path in Options.");
        return py;
    }

    static bool? _venvWorks;

    /// <summary>True only if the managed venv's interpreter exists AND runs (its base Python may have been deleted).</summary>
    public bool IsManagedVenvPresent => _venvWorks ??= File.Exists(Paths.VenvPython) && WorksAsPython(Paths.VenvPython);

    public static InstallVariant DefaultVariant()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            var exe = OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi";
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                try { if (File.Exists(Path.Combine(d, exe))) return InstallVariant.NvidiaCuda; } catch { }
        }
        return OperatingSystem.IsWindows() ? InstallVariant.DirectML : InstallVariant.Cpu;
    }

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
        _venvWorks = null;
        if (!IsManagedVenvPresent)
        {
            if (Directory.Exists(Paths.Venv))
            {
                log("The existing virtual environment is broken (its Python is gone); recreating it.");
                try { Directory.Delete(Paths.Venv, true); }
                catch (Exception e) { log($"Could not remove the old venv: {e.Message}"); return false; }
            }
            var sys = await EnsureSystemPythonAsync(log);
            if (sys == null) return false;
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
        log($"Installing {extra} and yt-dlp (this can take several minutes)...");
        int code = await ProcessRunner.RunAsync(Paths.VenvPython,
            new[] { "-m", "pip", "install", "--upgrade", extra, "yt-dlp", "audioread" }, log);
        _venvWorks = null;
        log(code == 0 ? "Install finished." : $"pip exited with code {code}.");
        if (!HasFfmpeg()) log("WARNING: ffmpeg was not found on PATH. It is required for both separation and YouTube import.");
        return code == 0;
    }
}
