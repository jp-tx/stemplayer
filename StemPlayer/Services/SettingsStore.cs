using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StemPlayer.Models;

namespace StemPlayer.Services;

public static class SettingsStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        try { if (File.Exists(Paths.Settings)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings), Opts) ?? new(); }
        catch { }
        return new();
    }

    public static void Save(AppSettings s) => File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(s, Opts));

    public static string LibraryDir(AppSettings s)
    {
        var dir = string.IsNullOrWhiteSpace(s.LibraryDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "StemPlayer")
            : s.LibraryDir.Trim().Trim('"');
        try { return Directory.CreateDirectory(dir).FullName; }
        catch (IOException)
        {
            // A bad saved path (e.g. one that slipped through before trimming was added) must
            // not permanently crash the app on every startup with no way back into Settings.
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "StemPlayer");
            return Directory.CreateDirectory(fallback).FullName;
        }
    }
}
