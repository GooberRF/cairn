using System.Diagnostics;
using System.Runtime.InteropServices;
using Cairn.Formats.Audio;

namespace Cairn.Snd.Ui.Playback;

/// <summary>Where a <see cref="SoundPlayer"/> is.</summary>
public enum PlayerState { Stopped, Playing, Paused }

/// <summary>
/// Plays 16-bit PCM with sample-accurate positions and seamless loops (the loop region repeats while
/// <see cref="Looping"/> is on). Thread-safe: the UI thread calls it, and playing ends on a worker thread
/// (<see cref="Ended"/> is raised there).
/// </summary>
public abstract class SoundPlayer : IDisposable
{
    protected readonly object Gate = new();
    private double _volume = 0.8;

    protected SoundPlayer(short[] samples, int channels, int rate)
    {
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        Channels = Math.Max(1, channels);
        Rate = Math.Max(1, rate);
        Frames = samples.LongLength / Channels;
    }

    public short[] Samples { get; }
    public int Channels { get; }
    public int Rate { get; }
    public long Frames { get; }

    /// <summary>The loop region (frames), or null for none.</summary>
    public SoundLoop? LoopRegion { get; set; }

    /// <summary>True to repeat the loop region (the whole sound when it has none) instead of stopping at the end.</summary>
    public bool Looping { get; set; }

    /// <summary>Volume 0..1, applied to the samples as they are sent.</summary>
    public double Volume { get => _volume; set => _volume = Math.Clamp(value, 0, 1); }

    /// <summary>Why playing is impossible (no audio device), or null.</summary>
    public string? Failure { get; protected set; }

    public abstract PlayerState State { get; }

    /// <summary>The frame being heard now.</summary>
    public abstract long Position { get; }

    /// <summary>Raised (on a worker thread) when playing reaches the end of a sound that does not loop.</summary>
    public event EventHandler? Ended;

    protected void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);

    /// <summary>Plays from the current position (from the start after the end).</summary>
    public abstract void Play();
    public abstract void Pause();
    /// <summary>Stops and goes back to the start.</summary>
    public abstract void Stop();
    /// <summary>Moves the play position (playing continues from there).</summary>
    public abstract void Seek(long frame);

    /// <summary>(start, end) of what repeats: the loop region, else the whole sound.</summary>
    protected (long Start, long End) RepeatRange() =>
        LoopRegion is { } l && l.End > l.Start && l.End <= Frames ? (l.Start, l.End) : (0, Frames);

    /// <summary>
    /// Copies frames from <paramref name="cursor"/> into <paramref name="target"/> (16-bit, interleaved) with the volume,
    /// wrapping at the loop end while looping. Returns the frames written and moves the cursor; records each run's
    /// (target frame offset, source frame) in <paramref name="runs"/>.
    /// </summary>
    protected int Fill(Span<short> target, ref long cursor, List<(int At, long Source)>? runs)
    {
        int capacity = target.Length / Channels, written = 0;
        double volume = _volume;
        bool looping = Looping;
        var (loopStart, loopEnd) = RepeatRange();
        while (written < capacity)
        {
            // at the loop end: back to the loop start (past it, after a seek, the sound plays to its end first)
            if (looping && loopEnd > loopStart && cursor == loopEnd) { cursor = loopStart; continue; }
            long end = looping && cursor < loopEnd ? loopEnd : Frames;
            if (cursor >= end)
            {
                if (!looping || loopEnd <= loopStart) break;
                cursor = loopStart;
                continue;
            }
            int n = (int)Math.Min(capacity - written, end - cursor);
            runs?.Add((written, cursor));
            var source = Samples.AsSpan((int)(cursor * Channels), n * Channels);
            var dest = target.Slice(written * Channels, n * Channels);
            if (volume >= 0.999) source.CopyTo(dest);
            else for (int i = 0; i < source.Length; i++) dest[i] = (short)(source[i] * volume);
            written += n;
            cursor += n;
        }
        return written;
    }

    public abstract void Dispose();

    /// <summary>A waveOut player, or a silent one when <paramref name="silent"/> (diagnostic runs) or no device opens.</summary>
    public static SoundPlayer Create(short[] samples, int channels, int rate, bool silent) =>
        silent ? new SilentPlayer(samples, channels, rate) : new WaveOutPlayer(samples, channels, rate);
}

/// <summary>A player that only keeps time (diagnostic runs: positions, loops and the end behave as when heard).</summary>
public sealed class SilentPlayer(short[] samples, int channels, int rate) : SoundPlayer(samples, channels, rate)
{
    private readonly Stopwatch _clock = new();
    private long _from;
    private PlayerState _state;
    private Timer? _endTimer;

    public override PlayerState State { get { lock (Gate) { Update(); return _state; } } }

    public override long Position { get { lock (Gate) { return Update(); } } }

    /// <summary>The position now; ends playing past the end.</summary>
    private long Update()
    {
        if (_state != PlayerState.Playing) return _from;
        long at = _from + (long)(_clock.Elapsed.TotalSeconds * Rate);
        var (start, end) = RepeatRange();
        if (Looping && at >= end && end > start && _from < end) return start + (at - end) % (end - start);
        if (at >= Frames)
        {
            _state = PlayerState.Stopped;
            _clock.Reset();
            _from = Frames;
            return Frames;
        }
        return at;
    }

    public override void Play()
    {
        lock (Gate)
        {
            if (_state == PlayerState.Playing) return;
            if (_from >= Frames) _from = 0;
            _state = PlayerState.Playing;
            _clock.Restart();
            ArmEnd();
        }
    }

    private void ArmEnd()
    {
        _endTimer?.Dispose();
        _endTimer = new Timer(_ =>
        {
            bool ended;
            lock (Gate) { long at = Update(); ended = _state == PlayerState.Stopped && at >= Frames; }
            if (ended) { RaiseEnded(); return; }
            lock (Gate) if (_state == PlayerState.Playing) _endTimer?.Change(50, Timeout.Infinite);
        }, null, 50, Timeout.Infinite);
    }

    public override void Pause()
    {
        lock (Gate)
        {
            if (_state != PlayerState.Playing) return;
            _from = Update();
            if (_state == PlayerState.Playing) _state = PlayerState.Paused;
            _clock.Reset();
        }
    }

    public override void Stop()
    {
        lock (Gate)
        {
            _state = PlayerState.Stopped;
            _from = 0;
            _clock.Reset();
        }
    }

    public override void Seek(long frame)
    {
        lock (Gate)
        {
            _from = Math.Clamp(frame, 0, Frames);
            if (_state == PlayerState.Playing) _clock.Restart();
        }
    }

    public override void Dispose()
    {
        lock (Gate)
        {
            _endTimer?.Dispose();
            _endTimer = null;
            _state = PlayerState.Stopped;
        }
    }
}

/// <summary>
/// Plays through the Windows waveOut API: a few short buffers kept queued by a worker thread, so loops are seamless and
/// the position is exact (the device's sample counter mapped back through the queued runs).
/// </summary>
public sealed class WaveOutPlayer : SoundPlayer
{
    private const int BufferCount = 4, BufferFrames = 2048;
    private const int WhdrDone = 1;
    private const uint WaveMapper = unchecked((uint)-1);
    private const int CallbackEvent = 0x00050000;
    private const int TimeSamples = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct MmTime
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Value;
        [FieldOffset(8)] public uint Padding;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr handle, uint device, ref WaveFormatEx format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutPause(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutRestart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr handle, ref MmTime time, int size);

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHdr>();

    private readonly AutoResetEvent _event = new(false);
    private readonly IntPtr[] _headers = new IntPtr[BufferCount];
    private readonly IntPtr[] _data = new IntPtr[BufferCount];
    private readonly bool[] _queued = new bool[BufferCount];
    private readonly short[] _scratch;
    // runs written since the last reset: (device frame they start at, source frame, frames)
    private readonly List<(long Out, long Source, int Frames)> _runs = [];
    private IntPtr _device;
    private Thread? _worker;
    private volatile bool _closing;
    private int _disposed;
    private PlayerState _state;
    private long _cursor, _outWritten, _resting;
    private long _pausedAt;

    /// <summary>How long a paused player keeps its device before letting it go (playing again reopens it).</summary>
    internal static TimeSpan PausedRelease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>True while the waveOut device is open (and its worker thread runs): only while playing or briefly paused.</summary>
    internal bool IsDeviceOpen { get { lock (Gate) return _device != IntPtr.Zero; } }

    /// <summary>True while the worker thread runs.</summary>
    internal bool HasWorker => _worker is { IsAlive: true };

    public WaveOutPlayer(short[] samples, int channels, int rate) : base(samples, channels, rate) => _scratch = new short[BufferFrames * Channels];

    public override PlayerState State { get { lock (Gate) return _state; } }

    public override long Position
    {
        get
        {
            lock (Gate)
            {
                if (_state != PlayerState.Playing && _state != PlayerState.Paused) return _resting;
                return MapPlayed(Played());
            }
        }
    }

    private long Played()
    {
        if (_device == IntPtr.Zero) return 0;
        var time = new MmTime { Type = TimeSamples };
        return waveOutGetPosition(_device, ref time, Marshal.SizeOf<MmTime>()) == 0 && time.Type == TimeSamples ? time.Value : 0;
    }

    private long MapPlayed(long played)
    {
        if (_runs.Count == 0) return _resting;
        for (int i = _runs.Count - 1; i >= 0; i--)
        {
            var run = _runs[i];
            if (played >= run.Out) return run.Source + Math.Min(played - run.Out, Math.Max(0, run.Frames - 1));
        }
        return _runs[0].Source;
    }

    private bool EnsureOpen()
    {
        if (_device != IntPtr.Zero) return true;
        if (Failure is not null || _disposed != 0) return false;
        var format = new WaveFormatEx
        {
            FormatTag = 1, Channels = (ushort)Channels, SamplesPerSec = (uint)Rate, BitsPerSample = 16,
            BlockAlign = (ushort)(Channels * 2), AvgBytesPerSec = (uint)(Rate * Channels * 2),
        };
        int result = waveOutOpen(out _device, WaveMapper, ref format, _event.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent);
        if (result != 0)
        {
            _device = IntPtr.Zero;
            Failure = $"no audio output could be opened (waveOut error {result})";
            return false;
        }
        for (int i = 0; i < BufferCount; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BufferFrames * Channels * 2);
            _headers[i] = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHdr { Data = _data[i], BufferLength = (uint)(BufferFrames * Channels * 2) }, _headers[i], false);
            waveOutPrepareHeader(_device, _headers[i], HeaderSize);
        }
        _worker = new Thread(Work) { IsBackground = true, Name = "Cairn sound player" };
        _worker.Start();
        return true;
    }

    public override void Play()
    {
        lock (Gate)
        {
            if (_state == PlayerState.Playing) return;
            // a pause that outlasted PausedRelease let the device go: play on from where it stopped
            bool reopened = _device == IntPtr.Zero;
            if (!EnsureOpen()) return;
            if (_state == PlayerState.Paused && !reopened)
            {
                _state = PlayerState.Playing;
                if (!_queued.Any(q => q)) Refill();
                waveOutRestart(_device);
                return;
            }
            if (_resting >= Frames) _resting = 0;
            StartAt(_resting);
            _state = PlayerState.Playing;
            Refill();
        }
        _event.Set();
    }

    public override void Pause()
    {
        lock (Gate)
        {
            if (_state != PlayerState.Playing || _device == IntPtr.Zero) return;
            waveOutPause(_device);
            _state = PlayerState.Paused;
            _pausedAt = Stopwatch.GetTimestamp();
        }
    }

    public override void Stop()
    {
        lock (Gate)
        {
            ResetDevice();
            _state = PlayerState.Stopped;
            _resting = 0;
        }
    }

    public override void Seek(long frame)
    {
        frame = Math.Clamp(frame, 0, Frames);
        lock (Gate)
        {
            if (_state == PlayerState.Stopped) { _resting = frame; return; }
            bool paused = _state == PlayerState.Paused;
            StartAt(frame);
            _resting = frame;
            if (!paused) Refill();
            else if (_device != IntPtr.Zero) waveOutPause(_device);
        }
        if (_disposed == 0) _event.Set();
    }

    /// <summary>Drops what is queued and starts the device's count again at <paramref name="frame"/>. Under the lock.</summary>
    private void StartAt(long frame)
    {
        ResetDevice();
        _cursor = frame;
        _resting = frame;
    }

    private void ResetDevice()
    {
        if (_device != IntPtr.Zero) waveOutReset(_device);
        Array.Clear(_queued);
        _runs.Clear();
        _outWritten = 0;
    }

    /// <summary>Queues every free buffer. Under the lock, while playing.</summary>
    private void Refill()
    {
        for (int i = 0; i < BufferCount; i++)
        {
            if (_queued[i] && (Marshal.PtrToStructure<WaveHdr>(_headers[i]).Flags & WhdrDone) == 0) continue;
            _queued[i] = false;
            var runs = new List<(int At, long Source)>();
            int frames = Fill(_scratch, ref _cursor, runs);
            if (frames == 0) continue;
            for (int r = 0; r < runs.Count; r++)
            {
                int length = (r + 1 < runs.Count ? runs[r + 1].At : frames) - runs[r].At;
                _runs.Add((_outWritten + runs[r].At, runs[r].Source, length));
            }
            _outWritten += frames;
            Marshal.Copy(_scratch, 0, _data[i], frames * Channels);
            var header = Marshal.PtrToStructure<WaveHdr>(_headers[i]);
            header.BufferLength = (uint)(frames * Channels * 2);
            header.Flags &= ~(uint)WhdrDone;
            Marshal.StructureToPtr(header, _headers[i], false);
            if (waveOutWrite(_device, _headers[i], HeaderSize) == 0) _queued[i] = true;
        }
        // keep the run list short: drop runs the device has played past
        long played = Played();
        while (_runs.Count > 2 && _runs[1].Out <= played) _runs.RemoveAt(0);
    }

    private void Work()
    {
        while (!_closing)
        {
            _event.WaitOne(40);
            if (_closing) break;
            bool ended = false;
            lock (Gate)
            {
                // Stopped (or paused for a while): let the device and this thread go; Play opens them again.
                if (_state == PlayerState.Stopped
                    || (_state == PlayerState.Paused && Stopwatch.GetElapsedTime(_pausedAt) >= PausedRelease))
                {
                    if (_state == PlayerState.Paused) _resting = MapPlayed(Played());
                    CloseDevice();
                    if (ReferenceEquals(_worker, Thread.CurrentThread)) _worker = null;
                    return;
                }
                if (_state != PlayerState.Playing) continue;
                Refill();
                bool anyQueued = false;
                for (int i = 0; i < BufferCount; i++)
                    if (_queued[i] && (Marshal.PtrToStructure<WaveHdr>(_headers[i]).Flags & WhdrDone) == 0) anyQueued = true;
                if (!anyQueued)
                {
                    // nothing more to play: the end of a sound that does not loop
                    ResetDevice();
                    _state = PlayerState.Stopped;
                    _resting = Frames;
                    ended = true;
                }
            }
            if (ended) RaiseEnded();
        }
    }

    /// <summary>Closes the device and frees its buffers (the position is kept in <c>_resting</c>). Under the lock.</summary>
    private void CloseDevice()
    {
        if (_device != IntPtr.Zero)
        {
            waveOutReset(_device);
            for (int i = 0; i < BufferCount; i++) if (_headers[i] != IntPtr.Zero) waveOutUnprepareHeader(_device, _headers[i], HeaderSize);
            waveOutClose(_device);
            _device = IntPtr.Zero;
        }
        for (int i = 0; i < BufferCount; i++)
        {
            if (_headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_headers[i]);
            if (_data[i] != IntPtr.Zero) Marshal.FreeHGlobal(_data[i]);
            _headers[i] = _data[i] = IntPtr.Zero;
        }
        Array.Clear(_queued);
        _runs.Clear();
        _outWritten = 0;
    }

    /// <summary>Stops and frees everything; a second call does nothing.</summary>
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closing = true;
        _event.Set();
        _worker?.Join(1000);
        lock (Gate)
        {
            CloseDevice();
            _state = PlayerState.Stopped;
        }
        _event.Dispose();
    }
}
