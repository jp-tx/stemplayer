using System;
using System.IO;

namespace StemPlayer.Services;

public static class Paths
{
    public static string AppData { get; } = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StemPlayer")).FullName;

    public static string Settings => Path.Combine(AppData, "settings.json");
    public static string LibraryJson => Path.Combine(AppData, "library.json");
    public static string ListsJson => Path.Combine(AppData, "lists.json");
    public static string CacheJson => Path.Combine(AppData, "cache.json");
    public static string Venv => Path.Combine(AppData, "venv");
    public static string Models => Directory.CreateDirectory(Path.Combine(AppData, "models")).FullName;
    public static string Runner => Path.Combine(AppData, "runner.py");
    public static string BeatDetectScript => Path.Combine(AppData, "beat_detect.py");

    public static string VenvPython => OperatingSystem.IsWindows()
        ? Path.Combine(Venv, "Scripts", "python.exe")
        : Path.Combine(Venv, "bin", "python");

    public static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim().TrimEnd('.');
        return s.Length == 0 ? "untitled" : (s.Length > 80 ? s[..80] : s);
    }
}
