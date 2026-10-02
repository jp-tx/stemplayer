using System;
using System.Collections.Generic;
using System.Threading;
using Silk.NET.OpenAL;

namespace StemPlayer.Services;

/// <summary>Streams a <see cref="StemMixer"/> to the default audio device through OpenAL Soft (Windows + Linux + macOS).</summary>
public sealed unsafe class OpenAlOutput : IDisposable
{
    const int BufferCount = 4;
    const int FramesPerBuffer = 2048; // ~46 ms each, ~185 ms total fader latency

    readonly AL _al;
    readonly ALContext _alc;
    readonly Device* _device;
    readonly Context* _ctx;
    readonly uint _source;
    readonly uint[] _buffers = new uint[BufferCount];
    readonly Queue<(uint buf, int frames)> _queued = new();
    readonly Queue<uint> _free = new();
    readonly object _lock = new();

    StemMixer? _mixer;
    Thread? _thread;
    volatile bool _stop;
    volatile bool _playing;
    volatile bool _ended;
    float _volume = 1f;

    public bool IsPlaying => _playing;
    public bool Ended => _ended;
    public event Action? TrackEnded;

    public OpenAlOutput()
    {
        _alc = ALContext.GetApi();
        _al = AL.GetApi();
        _device = _alc.OpenDevice("");
        if (_device == null) throw new InvalidOperationException("No audio output device found (OpenAL).");
        _ctx = _alc.CreateContext(_device, null);
        _alc.MakeContextCurrent(_ctx);
        _source = _al.GenSource();
        for (int i = 0; i < BufferCount; i++) { _buffers[i] = _al.GenBuffer(); _free.Enqueue(_buffers[i]); }
    }

    public void Load(StemMixer mixer)
    {
        Stop();
        _mixer?.Dispose();
        _mixer = mixer;
        _ended = false;
    }

    public long PositionFrames
    {
        get
        {
            if (_mixer == null) return 0;
            lock (_lock)
            {
                _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int off);
                long buffered = 0;
                foreach (var q in _queued) buffered += q.frames;
                // _queued still contains buffers already fully played but not yet unqueued; the sample offset is
                // relative to the start of the queue, so subtract it directly.
                long pos = _mixer.PositionFrames - Math.Max(0, buffered - off);
                return Math.Clamp(pos, 0, _mixer.TotalFrames);
            }
        }
    }

    public void SetVolume(float v) { _volume = v; _al.SetSourceProperty(_source, SourceFloat.Gain, v); }

    public void Play()
    {
        if (_mixer == null) return;
        if (_ended) { _mixer.SeekFrames(0); _ended = false; }
        if (_thread is { IsAlive: true }) { _playing = true; lock (_lock) _al.SourcePlay(_source); return; }
        _stop = false; _playing = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "OpenAL pump" };
        _thread.Start();
    }

    public void Pause()
    {
        _playing = false;
        lock (_lock) _al.SourcePause(_source);
    }

    public void Stop()
    {
        _stop = true; _playing = false;
        _thread?.Join(500);
        _thread = null;
        lock (_lock) Flush();
    }

    public void Seek(long frame)
    {
        if (_mixer == null) return;
        lock (_lock)
        {
            Flush();
            _mixer.SeekFrames(frame);
            _ended = false;
        }
        if (_playing && _thread is { IsAlive: true }) { /* pump refills and restarts source */ }
    }

    // Must hold _lock.
    void Flush()
    {
        _al.SourceStop(_source);
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
        if (queued > 0)
        {
            var tmp = new uint[queued];
            fixed (uint* p = tmp) _al.SourceUnqueueBuffers(_source, queued, p);
        }
        _queued.Clear(); _free.Clear();
        foreach (var b in _buffers) _free.Enqueue(b);
    }

    void Pump()
    {
        var floats = new float[FramesPerBuffer * StemMixer.Channels];
        var shorts = new short[floats.Length];
        bool drained = false;

        while (!_stop)
        {
            if (!_playing) { Thread.Sleep(20); continue; }
            lock (_lock)
            {
                _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
                while (processed-- > 0)
                {
                    uint b = 0;
                    _al.SourceUnqueueBuffers(_source, 1, &b);
                    if (_queued.Count > 0) _queued.Dequeue();
                    _free.Enqueue(b);
                }

                while (_free.Count > 0 && !drained && _mixer != null)
                {
                    int n = _mixer.Read(floats, floats.Length);
                    if (n <= 0) { drained = true; break; }
                    for (int i = 0; i < n; i++)
                        shorts[i] = (short)(Math.Clamp(floats[i], -1f, 1f) * short.MaxValue);
                    uint buf = _free.Dequeue();
                    fixed (short* sp = shorts)
                        _al.BufferData(buf, BufferFormat.Stereo16, sp, n * sizeof(short), StemMixer.SampleRate);
                    _al.SourceQueueBuffers(_source, 1, &buf);
                    _queued.Enqueue((buf, n / StemMixer.Channels));
                }

                _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int q);
                if (q > 0 && state != (int)SourceState.Playing) _al.SourcePlay(_source);
                if (drained && q == 0) { _playing = false; _ended = true; TrackEnded?.Invoke(); return; }
            }
            // A seek flushes everything; allow reading again.
            if (drained && _mixer != null && _mixer.PositionFrames < _mixer.TotalFrames) drained = false;
            Thread.Sleep(8);
        }
    }

    public void Dispose()
    {
        Stop();
        _mixer?.Dispose();
        _al.DeleteSource(_source);
        foreach (var b in _buffers) _al.DeleteBuffer(b);
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_ctx);
        _alc.CloseDevice(_device);
        _al.Dispose(); _alc.Dispose();
    }
}
