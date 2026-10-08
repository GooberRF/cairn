using System.Buffers.Binary;
using System.Globalization;

namespace Cairn.Formats.Audio;

/// <summary>Which PlayStation 2 sound layout a file has.</summary>
public enum Ps2SoundLayout
{
    /// <summary>A sound effect (.vse) with the 24-byte header.</summary>
    Vse,
    /// <summary>A sound effect (.vse) with the older 12-byte header (duration, data size, pitch).</summary>
    VseOld,
    /// <summary>Music (.vmu): 12-byte header, stereo, 16 KB blocks per channel.</summary>
    Vmu,
}

/// <summary>
/// Red Faction's PlayStation 2 sounds, decoded to PCM. Both are SPU ADPCM (<see cref="PsAdpcm"/>):
/// <list type="bullet">
/// <item><c>.vse</c> (sound effect, mono). Current header, 24 bytes: u32 sound time in ms (1 for looping sounds), u32
/// key-off time in ms (0 when looping), u32 envelope (0x000F0003), u32 data size, u32 loop start, u16 SPU pitch, u16 flags
/// (bit 1 looping). Older header, 12 bytes: u32 duration in ms, u32 data size, u32 SPU pitch; their data is padded with
/// silence to at least 12,288 bytes. One-shot sounds end on a frame flagged "end" followed by a never-played end marker;
/// looping sounds end on a "loop end + repeat" frame. Sounds longer than 64 KB carry loop-start/loop-end flags every
/// 4,096 frames (the streaming buffer halves), which are not loops.</item>
/// <item><c>.vmu</c> (music, stereo, 44.1 kHz). Header, 12 bytes: u16 block count (data size / 32 KB rounded up), u16
/// SPU pitch (bit 15: the music loops), u32 block time, u16 loop start block, u16 loop start offset. The data alternates
/// 16 KB of left and 16 KB of right; the last, shorter block is split in half.</item>
/// </list>
/// The SPU pitch is the rate × 4096 / 48,000 (0x0759 = 22,050 Hz). Never written: Cairn reads and converts these only.
/// </summary>
public static class Ps2Sound
{
    /// <summary>Bytes per channel per .vmu block.</summary>
    public const int VmuInterleave = 0x4000;

    private const int VseHeader = 24, OldHeader = 12, VmuHeader = 12;
    private const uint UsualEnvelope = 0x000F0003;

    /// <summary>True for the PlayStation 2 sound extensions (.vse, .vmu).</summary>
    public static bool IsPs2SoundName(string name)
    {
        string ext = Path.GetExtension(name ?? string.Empty);
        return ext.Equals(".vse", StringComparison.OrdinalIgnoreCase) || ext.Equals(".vmu", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decodes a .vse or .vmu (chosen by the name's extension) to 16-bit PCM.</summary>
    /// <exception cref="AssetFormatException">Empty, too short for a header, or no recognisable header.</exception>
    public static DecodedSound Decode(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        return Run(bytes, name, decode: true).ToSound(bytes.LongLength);
    }

    /// <summary>Format, rate, channels and duration without decoding the samples (the frame flags are walked).</summary>
    /// <exception cref="AssetFormatException">Empty, too short for a header, or no recognisable header.</exception>
    public static AudioInfo Probe(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var run = Run(bytes, name, decode: false);
        string note = run.Layout switch
        {
            Ps2SoundLayout.VseOld => "older 12-byte header",
            _ => string.Empty,
        };
        if (run.Loop is { Enabled: true }) note = note.Length == 0 ? "loops" : note + ", loops";
        return new AudioInfo(run.Layout == Ps2SoundLayout.Vmu ? "VMU (PS2 music)" : "VSE (PS2 sound effect)", "PS ADPCM", run.Rate, run.Channels, null,
            run.Rate > 0 ? TimeSpan.FromSeconds((double)run.Frames / run.Rate) : null, null, note.Length == 0 ? null : note);
    }

    /// <summary>The layout a file has (by extension and header), or null when the header is not recognisable.</summary>
    public static Ps2SoundLayout? DetectLayout(ReadOnlySpan<byte> b, string name)
    {
        if (Path.GetExtension(name).Equals(".vmu", StringComparison.OrdinalIgnoreCase)) return b.Length >= VmuHeader ? Ps2SoundLayout.Vmu : null;
        long size = b.Length;
        if (size >= VseHeader && U32(b, 12) == size - VseHeader) return Ps2SoundLayout.Vse;
        if (size >= OldHeader && U32(b, 4) == size - OldHeader && PlausiblePitch(U32(b, 8))) return Ps2SoundLayout.VseOld;
        // damaged sizes: the current layout's envelope marker, else a plausible pitch where either layout keeps it
        if (size >= VseHeader && U32(b, 8) == UsualEnvelope) return Ps2SoundLayout.Vse;
        if (size >= VseHeader && U32(b, 16) == 0 && PlausiblePitch(U16(b, 20)) && U32(b, 12) > 0) return Ps2SoundLayout.Vse;
        if (size >= OldHeader && PlausiblePitch(U32(b, 8)) && U32(b, 4) > 0) return Ps2SoundLayout.VseOld;
        return null;
    }

    private static bool PlausiblePitch(long pitch) => pitch is >= 0x80 and <= 0x4000;
    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    private static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
    private static string Hz(double rate) => rate.ToString("N0", CultureInfo.InvariantCulture) + " Hz";
    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>What a run over the data found.</summary>
    private sealed class Result
    {
        public string Name = string.Empty;
        public Ps2SoundLayout Layout;
        public int Rate, Channels = 1, Pitch;
        public long Frames;
        public short[] Samples = [];
        public SoundLoop? Loop;
        public readonly List<(string, string)> Details = [];
        public readonly List<string> Quirks = [];
        public readonly List<SoundProblem> Problems = [];

        public void Problem(string code, SoundSeverity severity, string message) => Problems.Add(new(code, severity, message));

        public DecodedSound ToSound(long size) => new()
        {
            Name = Name,
            Format = Layout == Ps2SoundLayout.Vmu ? "PS2 music (.vmu)" : "PS2 sound effect (.vse)",
            Codec = "PS ADPCM",
            SampleRate = Rate,
            Channels = Channels,
            BitDepth = "4-bit ADPCM (decodes to 16-bit)",
            Samples = Samples,
            Loop = Loop,
            IsLossySource = true,
            FileSize = size,
            ExactRate = Pitch > 0 && Math.Abs(PsAdpcm.ExactRateFromPitch(Pitch) - Rate) >= 0.5 ? PsAdpcm.ExactRateFromPitch(Pitch) : null,
            Details = Details,
            Quirks = Quirks,
            Problems = Problems,
        };
    }

    private static Result Run(ReadOnlySpan<byte> b, string name, bool decode)
    {
        if (b.Length == 0) throw new AssetFormatException($"'{name}' is empty.");
        var layout = DetectLayout(b, name)
            ?? throw new AssetFormatException(b.Length < OldHeader
                ? $"'{name}' is {b.Length} bytes long, too short for a PS2 sound header."
                : $"'{name}' has no recognisable PS2 sound header.");
        var r = new Result { Name = name, Layout = layout };
        r.Quirks.Add("PS ADPCM is a lossy 4-bit coding of 16-bit sound: 28 samples in every 16-byte frame.");
        if (layout == Ps2SoundLayout.Vmu) RunVmu(b, r, decode);
        else RunVse(b, r, layout, decode);
        if (r.Pitch > 0 && Math.Abs(PsAdpcm.ExactRateFromPitch(r.Pitch) - r.Rate) >= 0.5)
            r.Quirks.Add($"The rate is stored as an SPU pitch (0x{r.Pitch:X4}): the console plays at {PsAdpcm.ExactRateFromPitch(r.Pitch).ToString("N1", CultureInfo.InvariantCulture)} Hz, written as the standard {Hz(r.Rate)}.");
        return r;
    }

    // ---- .vse ----------------------------------------------------------------------------------------------------

    private static void RunVse(ReadOnlySpan<byte> b, Result r, Ps2SoundLayout layout, bool decode)
    {
        long size = b.Length;
        int headerSize, pitch;
        long declared;
        bool looping;
        long? headerMs = null;
        if (layout == Ps2SoundLayout.Vse)
        {
            uint time = U32(b, 0), keyOff = U32(b, 4), envelope = U32(b, 8), loopStart = U32(b, 16);
            ushort flags = U16(b, 22);
            headerSize = VseHeader;
            declared = U32(b, 12);
            pitch = U16(b, 20);
            looping = (flags & 2) != 0 || (time == 1 && keyOff == 0);
            if (!looping) headerMs = keyOff;
            r.Details.Add(("Header", "24 bytes"));
            r.Details.Add(("Sound time", looping && time == 1 ? "1 (looping sound)" : $"{N(time)} ms"));
            r.Details.Add(("Key-off time", $"{N(keyOff)} ms"));
            r.Details.Add(("Envelope (ADSR)", $"0x{envelope:X8}"));
            r.Details.Add(("Data size (header)", $"{N(declared)} bytes"));
            r.Details.Add(("Loop start (header)", N(loopStart)));
            r.Details.Add(("SPU pitch", $"0x{pitch:X4}"));
            r.Details.Add(("Flags", $"0x{flags:X4}" + ((flags & 2) != 0 ? " (looping)" : string.Empty)));
            if (envelope != UsualEnvelope)
                r.Problem("SND006", SoundSeverity.Info, $"The envelope field is 0x{envelope:X8}; stock sounds have 0x{UsualEnvelope:X8}.");
            if ((flags & 2) != 0 != (time == 1 && keyOff == 0))
                r.Problem("SND016", SoundSeverity.Info, "The looping flag and the sound time disagree; the sound is treated as looping.");
        }
        else
        {
            uint ms = U32(b, 0);
            headerSize = OldHeader;
            declared = U32(b, 4);
            pitch = (int)Math.Min(U32(b, 8), int.MaxValue);
            looping = false;
            headerMs = ms;
            r.Details.Add(("Header", "12 bytes (older layout)"));
            r.Details.Add(("Duration (header)", $"{N(ms)} ms"));
            r.Details.Add(("Data size (header)", $"{N(declared)} bytes"));
            r.Details.Add(("SPU pitch", $"0x{pitch:X4}"));
            r.Problem("SND011", SoundSeverity.Info, "This sound has the older 12-byte header (duration, data size, pitch).");
            r.Quirks.Add("Older 12-byte header: the data is padded with silence to at least 12,288 bytes; the padding past the header's duration is dropped.");
        }
        SetRate(r, pitch);
        long data = Math.Max(0, size - headerSize);
        if (declared != data)
        {
            r.Problem("SND002", SoundSeverity.Warning, declared > data
                ? $"The header says {N(declared)} bytes of sound data but only {N(data)} follow: the file is cut short."
                : $"The header says {N(declared)} bytes of sound data but {N(data)} follow; all of them are decoded.");
        }
        if (data % PsAdpcm.FrameBytes != 0)
            r.Problem("SND004", SoundSeverity.Warning, $"{data % PsAdpcm.FrameBytes} bytes at the end do not make a whole 16-byte frame and are ignored.");
        long frameCount = data / PsAdpcm.FrameBytes;
        r.Details.Add(("Frames", $"{N(frameCount)} of 16 bytes"));

        var frames = b.Slice(headerSize, (int)(frameCount * PsAdpcm.FrameBytes));
        var channel = DecodeChannel(frames, frameCount, stopAtLoopEnd: false, decode, r, "the sound");
        long samples = channel.Frames * PsAdpcm.SamplesPerFrame;
        short[] pcm = channel.Pcm;

        // where it ends
        r.Details.Add(("End", channel.EndKind switch
        {
            EndKind.Marker => $"end marker after frame {N(channel.Frames)}",
            EndKind.EndFlag => $"end flag on frame {N(channel.Frames)}",
            _ => "the end of the data (no end flag)",
        }));
        if (channel.EndKind == EndKind.Data && !looping && frameCount > 0)
            r.Problem("SND005", SoundSeverity.Warning, "The sound has no end flag: it ends where the data ends (it may be cut short).");

        if (looping)
        {
            long start = channel.FirstLoopStart >= 0 ? channel.FirstLoopStart * PsAdpcm.SamplesPerFrame : 0;
            long end = channel.LastLoopEnd >= 0 ? (channel.LastLoopEnd + 1) * PsAdpcm.SamplesPerFrame : samples;
            end = Math.Min(end, samples);
            if (start >= end) start = 0;
            r.Loop = new SoundLoop(start, end, true, channel.LastLoopEnd >= 0 ? "header (looping) and the frames' loop flags" : "header (looping); the whole sound");
            r.Details.Add(("Loop", start == 0 && end == samples ? "the whole sound" : $"samples {N(start)} to {N(end)}"));
            r.Quirks.Add("Loops are marked by frame flags (loop start, loop end + repeat), so loop points fall on 28-sample frame boundaries.");
        }
        else
        {
            if (channel.LoopEndFlags > 0)
            {
                r.Problem("SND014", SoundSeverity.Info, $"{N(channel.LoopEndFlags)} loop-end flag(s) mark the halves of the console's streaming buffer (every 4,096 frames); the sound does not loop.");
                r.Quirks.Add("Sounds longer than 64 KB carry loop flags every 4,096 frames for the console's streaming buffer; they are not loops.");
            }
            r.Quirks.Add("One-shot sounds end on a frame flagged \"end\", followed by an end-marker frame that is never played.");
        }

        // the older layout pads with silence: drop what lies past the header's duration (never anything audible)
        if (layout == Ps2SoundLayout.VseOld && headerMs is { } msOld && r.Rate > 0)
        {
            long wanted = (long)Math.Ceiling(msOld * (double)r.Rate / 1000);
            if (wanted < samples)
            {
                // never past a frame that holds sound
                long keep = Math.Min(samples, Math.Max(wanted, (channel.LastNonSilentFrame + 1) * PsAdpcm.SamplesPerFrame));
                if (keep < samples)
                {
                    r.Problem("SND012", SoundSeverity.Info, $"{N(samples - keep)} samples of silent padding past the header's duration ({N(msOld)} ms) are dropped.");
                    samples = keep;
                    if (decode) Array.Resize(ref pcm, (int)keep);
                }
            }
        }
        else if (headerMs is { } ms && r.Rate > 0 && ms > 0)
        {
            double decodedMs = samples * 1000.0 / r.Rate;
            if (Math.Abs(decodedMs - ms) > 50)
                r.Problem("SND008", SoundSeverity.Info, $"The header's time is {N(ms)} ms; the data plays for {N((long)Math.Round(decodedMs))} ms.");
        }
        r.Frames = samples;
        r.Samples = decode ? pcm : [];
        if (samples == 0) r.Problem("SND013", SoundSeverity.Warning, "The file holds no sound data.");
    }

    // ---- .vmu ----------------------------------------------------------------------------------------------------

    private static void RunVmu(ReadOnlySpan<byte> b, Result r, bool decode)
    {
        long size = b.Length;
        int blocks = U16(b, 0);
        int pitchField = U16(b, 2);
        uint blockTime = U32(b, 4);
        int loopBlock = U16(b, 8), loopOffset = U16(b, 10);
        bool looping = (pitchField & 0x8000) != 0;
        int pitch = pitchField & 0x7FFF;
        r.Channels = 2;
        r.Details.Add(("Header", "12 bytes"));
        r.Details.Add(("Blocks (header)", $"{N(blocks)} of 32 KB (16 KB per channel)"));
        r.Details.Add(("SPU pitch", $"0x{pitch:X4}" + (looping ? " + 0x8000 (loops)" : string.Empty)));
        r.Details.Add(("Block time (header)", N(blockTime)));
        r.Details.Add(("Loop start (header)", $"block {N(loopBlock)}, offset {N(loopOffset)}"));
        SetRate(r, pitch);

        long data = size - VmuHeader;
        long fullBlocks = data / (2L * VmuInterleave);
        long rest = data - fullBlocks * 2 * VmuInterleave;
        long expectedBlocks = fullBlocks + (rest > 0 ? 1 : 0);
        if (expectedBlocks != blocks)
            r.Problem("SND009", SoundSeverity.Warning, $"The header says {N(blocks)} blocks but the data makes {N(expectedBlocks)}.");
        long lastPerChannel = rest / 2 / PsAdpcm.FrameBytes * PsAdpcm.FrameBytes;
        if (rest != lastPerChannel * 2)
            r.Problem("SND010", SoundSeverity.Warning, $"The last block ({N(rest)} bytes) does not split into two whole halves; {N(rest - lastPerChannel * 2)} bytes are ignored.");
        r.Details.Add(("Last block", rest == 0 ? "full" : $"{N(lastPerChannel)} bytes per channel"));
        r.Quirks.Add("Stereo is stored as alternating 16 KB blocks of left and right; the last, shorter block is split in half.");
        if (looping) r.Quirks.Add("Bit 15 of the pitch field makes the music loop from the start.");
        r.Quirks.Add("The loop flags at each block's first and last frame are streaming-buffer markers, not loops.");

        long framesPerChannel = fullBlocks * (VmuInterleave / PsAdpcm.FrameBytes) + lastPerChannel / PsAdpcm.FrameBytes;
        var channels = new ChannelResult[2];
        for (int ch = 0; ch < 2; ch++)
        {
            var gathered = GatherVmuChannel(b, ch, fullBlocks, lastPerChannel);
            channels[ch] = DecodeChannel(gathered, framesPerChannel, stopAtLoopEnd: false, decode, r, ch == 0 ? "the left channel" : "the right channel", streamMarkers: true);
        }
        long frames = Math.Min(channels[0].Frames, channels[1].Frames);
        if (channels[0].Frames != channels[1].Frames)
            r.Problem("SND015", SoundSeverity.Warning, $"The channels end at different frames ({N(channels[0].Frames)} and {N(channels[1].Frames)}); the longer one is cut to the shorter.");
        long samples = frames * PsAdpcm.SamplesPerFrame;
        r.Details.Add(("End", channels[0].EndKind == EndKind.Marker ? "end markers in both channels" : "the end of the data"));
        r.Frames = samples;
        if (decode)
        {
            var pcm = new short[samples * 2];
            for (long i = 0; i < samples; i++)
            {
                pcm[i * 2] = channels[0].Pcm[i];
                pcm[i * 2 + 1] = channels[1].Pcm[i];
            }
            r.Samples = pcm;
        }
        if (looping && samples > 0)
        {
            r.Loop = new SoundLoop(0, samples, true, "header (pitch bit 15); the whole piece");
            r.Details.Add(("Loop", "the whole piece"));
        }
        if (samples == 0) r.Problem("SND013", SoundSeverity.Warning, "The file holds no sound data.");
    }

    /// <summary>One channel's frames of a .vmu, in order (16 KB from each 32 KB block, then its half of the last block).</summary>
    private static byte[] GatherVmuChannel(ReadOnlySpan<byte> b, int channel, long fullBlocks, long lastPerChannel)
    {
        var bytes = new byte[fullBlocks * VmuInterleave + lastPerChannel];
        long at = 0;
        for (long block = 0; block < fullBlocks; block++)
        {
            long from = VmuHeader + block * 2 * VmuInterleave + channel * VmuInterleave;
            b.Slice((int)from, VmuInterleave).CopyTo(bytes.AsSpan((int)at));
            at += VmuInterleave;
        }
        if (lastPerChannel > 0)
        {
            long from = VmuHeader + fullBlocks * 2 * VmuInterleave + channel * lastPerChannel;
            b.Slice((int)from, (int)lastPerChannel).CopyTo(bytes.AsSpan((int)at));
        }
        return bytes;
    }

    // ---- frames --------------------------------------------------------------------------------------------------

    private enum EndKind { Data, EndFlag, Marker }

    private sealed class ChannelResult
    {
        public long Frames;
        public short[] Pcm = [];
        public EndKind EndKind;
        public long FirstLoopStart = -1, LastLoopEnd = -1, LoopEndFlags, LastNonSilentFrame = -1;
    }

    /// <summary>
    /// Decodes one channel's frames: up to an end-marker frame (flags 7, not played), or through a frame flagged "end"
    /// without repeat, or to the end of the data. Loop flags are recorded; with <paramref name="streamMarkers"/> they are
    /// block markers and not reported.
    /// </summary>
    private static ChannelResult DecodeChannel(ReadOnlySpan<byte> frames, long frameCount, bool stopAtLoopEnd, bool decode, Result r, string what, bool streamMarkers = false)
    {
        var result = new ChannelResult();
        long played = frameCount;
        result.EndKind = EndKind.Data;
        for (long f = 0; f < frameCount; f++)
        {
            int flags = frames[(int)(f * PsAdpcm.FrameBytes + 1)];
            if (flags == PsAdpcm.EndMarker) { played = f; result.EndKind = EndKind.Marker; break; }
            if ((flags & PsAdpcm.FlagLoopStart) != 0 && result.FirstLoopStart < 0) result.FirstLoopStart = f;
            if ((flags & 3) == 3) { result.LastLoopEnd = f; result.LoopEndFlags++; if (stopAtLoopEnd) { played = f + 1; result.EndKind = EndKind.EndFlag; break; } }
            if ((flags & 3) == PsAdpcm.FlagLoopEnd) { played = f + 1; result.EndKind = EndKind.EndFlag; break; }
        }
        if (streamMarkers) { result.LoopEndFlags = 0; result.FirstLoopStart = -1; result.LastLoopEnd = -1; }
        result.Frames = played;

        // out-of-range frames, silence (for the older layout's padding), and the samples
        int bad = 0;
        long firstBad = -1;
        Span<short> block = stackalloc short[PsAdpcm.SamplesPerFrame];
        short[] pcm = decode ? new short[played * PsAdpcm.SamplesPerFrame] : [];
        int h1 = 0, h2 = 0;
        for (long f = 0; f < played; f++)
        {
            var frame = frames.Slice((int)(f * PsAdpcm.FrameBytes), PsAdpcm.FrameBytes);
            if (PsAdpcm.FilterOf(frame[0]) > PsAdpcm.MaxFilter || PsAdpcm.ShiftOf(frame[0]) > PsAdpcm.MaxShift)
            {
                bad++;
                if (firstBad < 0) firstBad = f;
            }
            // filter 0 and zero residuals decode to zeros whatever came before
            bool silent = PsAdpcm.FilterOf(frame[0]) == 0;
            for (int i = 2; silent && i < PsAdpcm.FrameBytes; i++) if (frame[i] != 0) { silent = false; break; }
            if (!silent) result.LastNonSilentFrame = f;
            if (decode)
            {
                PsAdpcm.DecodeFrame(frame, block, ref h1, ref h2, out _);
                block.CopyTo(pcm.AsSpan((int)(f * PsAdpcm.SamplesPerFrame)));
            }
        }
        result.Pcm = pcm;
        if (bad > 0)
            r.Problem("SND003", SoundSeverity.Warning, $"{N(bad)} frame(s) of {what} have an out-of-range filter or shift (first: frame {N(firstBad)}); decoded as the console does (filter 0, shift 9).");
        return result;
    }

    private static void SetRate(Result r, int pitch)
    {
        r.Pitch = pitch;
        if (PlausiblePitch(pitch))
        {
            r.Rate = PsAdpcm.RateFromPitch(pitch);
        }
        else
        {
            r.Rate = 22050;
            r.Problem("SND007", SoundSeverity.Warning, $"The SPU pitch 0x{pitch:X4} is implausible; the sound is played at 22,050 Hz.");
        }
        r.Details.Add(("Sample rate", PlausiblePitch(pitch) ? $"{Hz(r.Rate)} (the console plays {PsAdpcm.ExactRateFromPitch(pitch).ToString("N1", CultureInfo.InvariantCulture)} Hz)" : "unknown (22,050 Hz assumed)"));
    }
}
