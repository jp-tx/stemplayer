using System;
using System.Collections.Generic;

namespace StemPlayer.Models;

/// <summary>The five faders shown in the player.</summary>
public enum StemGroup { Vocals, Drums, Keys, Guitar, Bass, Other }

public enum DeviceMode { Gpu, Cpu }

public enum TrackState { Queued, Downloading, Separating, Ready, Failed }

public enum DeleteChoice { Cancel, EntryOnly, Everything }

public class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string? SourceUrl { get; set; }
    public string Model { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.UtcNow;

    /// <summary>Stem role (vocals, drums, bass, guitar, piano, other, instrumental) to file name inside the track folder.</summary>
    public Dictionary<string, string> Stems { get; set; } = new();
}

public class AppSettings
{
    public DeviceMode Device { get; set; } = DeviceMode.Gpu;
    public string Model { get; set; } = "htdemucs_6s.yaml";
    /// <summary>Optional override. Empty = use the managed virtualenv.</summary>
    public string PythonPath { get; set; } = "";
    public string LibraryDir { get; set; } = "";
    public int MaxConcurrentImports { get; set; } = 1;
}

public record ModelPreset(string FileName, string Name, string Description)
{
    public override string ToString() => Name;

    public static readonly ModelPreset[] Presets =
    {
        new("htdemucs_6s.yaml", "Demucs 6-stem (vocals, drums, bass, guitar, piano, other)", "Only built-in model with separate guitar and keys. Recommended."),
        new("htdemucs_ft.yaml", "Demucs 4-stem fine-tuned (vocals, drums, bass, other)", "Best Demucs quality, but guitar and keys stay in 'Other'."),
        new("htdemucs.yaml", "Demucs 4-stem (faster)", "Faster, slightly lower quality than the fine-tuned version."),
        new("model_bs_roformer_ep_317_sdr_12.9755.ckpt", "BS-Roformer (vocals / instrumental)", "State-of-the-art vocal isolation. No separate instruments."),
        new("UVR-MDX-NET-Inst_HQ_3.onnx", "MDX-Net Inst HQ 3 (vocals / instrumental)", "Fast, good vocal/instrumental split."),
    };
}
