using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using StemPlayer.Models;

namespace StemPlayer.Services;

/// <summary>
/// Reads every stem WAV in lock step and sums them into one stereo float stream,
/// applying per-group gain / mute / solo. Gain changes are smoothed to avoid zipper noise.
/// </summary>
public sealed class StemMixer : IDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    static readonly int GroupCount = Enum.GetValues<StemGroup>().Length;

    sealed class Source
    {
        public required WaveFileReader Reader;
        public required ISampleProvider Samples;
        public required StemGroup Group;
        public float[] Buf = Array.Empty<float>();
    }

    readonly List<Source> _sources = new();
    readonly float[] _target;
    readonly float[] _current;
    readonly object _lock = new();
    readonly bool[] _mute;
    readonly bool[] _solo;

    public long TotalFrames { get; private set; }
    public long PositionFrames { get; private set; }
    public WaveFormat Format { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static StemGroup? GroupFor(string role) => role.ToLowerInvariant() switch
    {
        "vocals" => StemGroup.Vocals,
        "drums" => StemGroup.Drums,
        "bass" => StemGroup.Bass,
        "guitar" => StemGroup.Guitar,
        "piano" or "keys" => StemGroup.Keys,
        "other" or "instrumental" => StemGroup.Other,
        _ => StemGroup.Other,
    };

    public StemMixer(string trackDir, Track track)
    {
        _target = new float[GroupCount];
        _current = new float[GroupCount];
        _mute = new bool[GroupCount];
        _solo = new bool[GroupCount];
        for (int i = 0; i < GroupCount; i++) _target[i] = _current[i] = 1f;
        foreach (var (role, file) in track.Stems)
        {
            var path = Path.Combine(trackDir, file);
            if (!File.Exists(path)) continue;
            var reader = new WaveFileReader(path);
            ISampleProvider sp = reader.ToSampleProvider();
            if (sp.WaveFormat.Channels == 1) sp = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(sp);
            // Stems from audio-separator are 44.1 kHz stereo; anything else would need resampling.
            if (sp.WaveFormat.SampleRate != SampleRate)
                throw new NotSupportedException($"Stem {file} is {sp.WaveFormat.SampleRate} Hz; expected {SampleRate} Hz.");
            _sources.Add(new Source { Reader = reader, Samples = sp, Group = GroupFor(role)!.Value });
        }
        if (_sources.Count == 0) throw new InvalidOperationException("Track has no playable stems.");
        TotalFrames = _sources.Max(s => s.Reader.Length / s.Reader.WaveFormat.BlockAlign);
    }

    public bool HasGroup(StemGroup g) => _sources.Any(s => s.Group == g);

    public void SetGain(StemGroup g, float gain) { lock (_lock) _target[(int)g] = gain; }
    public void SetMute(StemGroup g, bool m) { lock (_lock) _mute[(int)g] = m; }
    public void SetSolo(StemGroup g, bool solo) { lock (_lock) _solo[(int)g] = solo; }

    float EffectiveTarget(int g) => Array.IndexOf(_solo, true) >= 0 ? (_solo[g] ? _target[g] : 0f) : (_mute[g] ? 0f : _target[g]);

    public void SeekFrames(long frame)
    {
        lock (_lock)
        {
            frame = Math.Clamp(frame, 0, TotalFrames);
            foreach (var s in _sources)
                s.Reader.Position = Math.Min(frame * s.Reader.WaveFormat.BlockAlign, s.Reader.Length);
            PositionFrames = frame;
        }
    }

    /// <summary>Fills <paramref name="dest"/> with interleaved stereo floats. Returns the number of floats written (0 = end of track).</summary>
    public int Read(float[] dest, int count)
    {
        lock (_lock)
        {
            Array.Clear(dest, 0, count);
            int maxRead = 0;
            var tgt = new float[GroupCount];
            for (int g = 0; g < GroupCount; g++) tgt[g] = EffectiveTarget(g);
            int frames = count / Channels;

            foreach (var s in _sources)
            {
                if (s.Buf.Length < count) s.Buf = new float[count];
                int n = s.Samples.Read(s.Buf.AsSpan(0, count));
                if (n > maxRead) maxRead = n;
                int g = (int)s.Group;
                float cur = _current[g], to = tgt[g];
                // Ramp linearly across the block from the current gain to the target.
                float step = frames > 0 ? (to - cur) / frames : 0f;
                for (int f = 0; f < n / Channels; f++)
                {
                    float gain = cur + step * f;
                    dest[f * 2] += s.Buf[f * 2] * gain;
                    dest[f * 2 + 1] += s.Buf[f * 2 + 1] * gain;
                }
            }
            // Advance each group's gain once per block (after all of its sources used the same ramp).
            for (int g = 0; g < GroupCount; g++) _current[g] = tgt[g];

            PositionFrames += maxRead / Channels;
            return maxRead;
        }
    }

    public void Dispose() { foreach (var s in _sources) s.Reader.Dispose(); }
}
