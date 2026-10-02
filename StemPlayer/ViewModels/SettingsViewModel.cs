using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemPlayer.Models;
using StemPlayer.Services;

namespace StemPlayer.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    readonly AppSettings _s;
    public ModelPreset[] Presets => ModelPreset.Presets;
    public InstallVariant[] Variants { get; } = Enum.GetValues<InstallVariant>()
        .Where(v => v != InstallVariant.DirectML || OperatingSystem.IsWindows()).ToArray();
    public ObservableCollection<string> Output { get; } = new();

    public SettingsViewModel(AppSettings s)
    {
        _s = s;
        Model = s.Model;
        UseGpu = s.Device == DeviceMode.Gpu;
        PythonPath = s.PythonPath;
        LibraryDir = s.LibraryDir;
        SelectedPreset = Presets.FirstOrDefault(p => p.FileName == s.Model);
        SelectedVariant = Variants[0];
    }

    [ObservableProperty] public partial string Model { get; set; }
    [ObservableProperty] public partial ModelPreset? SelectedPreset { get; set; }
    [ObservableProperty] public partial bool UseGpu { get; set; }
    [ObservableProperty] public partial string PythonPath { get; set; }
    [ObservableProperty] public partial string LibraryDir { get; set; }
    [ObservableProperty] public partial InstallVariant SelectedVariant { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }

    public bool UseCpu { get => !UseGpu; set => UseGpu = !value; }
    public string PresetDescription => SelectedPreset?.Description ?? "Custom model file name (see: audio-separator --list_models).";

    partial void OnUseGpuChanged(bool value) => OnPropertyChanged(nameof(UseCpu));
    partial void OnSelectedPresetChanged(ModelPreset? value)
    {
        if (value != null) Model = value.FileName;
        OnPropertyChanged(nameof(PresetDescription));
    }

    public void Apply()
    {
        _s.Model = string.IsNullOrWhiteSpace(Model) ? "htdemucs_6s.yaml" : Model.Trim();
        _s.Device = UseGpu ? DeviceMode.Gpu : DeviceMode.Cpu;
        _s.PythonPath = PythonPath.Trim();
        _s.LibraryDir = LibraryDir.Trim().Trim('"');
    }

    void Write(string line) => Dispatcher.UIThread.Post(() => Output.Add(line));

    [RelayCommand]
    async Task Install()
    {
        Apply();
        Busy = true;
        try { await new PythonEnv(_s).InstallAsync(SelectedVariant, Write); }
        finally { Busy = false; }
    }

    /// <summary>Reports which accelerators the installed Python packages can actually use.</summary>
    [RelayCommand]
    async Task DetectGpu()
    {
        Apply();
        Busy = true;
        try
        {
            var env = new PythonEnv(_s);
            const string code =
                "import onnxruntime as o\n" +
                "print('onnxruntime providers:', ', '.join(o.get_available_providers()))\n" +
                "try:\n import torch\n print('torch CUDA/ROCm:', torch.cuda.is_available(), torch.cuda.get_device_name(0) if torch.cuda.is_available() else '')\n" +
                " print('torch MPS:', getattr(torch.backends,'mps',None) is not None and torch.backends.mps.is_available())\n" +
                "except Exception as e:\n print('torch:', e)";
            int rc = await ProcessRunner.RunAsync(env.Python, new[] { "-c", code }, Write);
            if (rc != 0) Write("Could not query the Python environment. Install it first.");
        }
        finally { Busy = false; }
    }
}
