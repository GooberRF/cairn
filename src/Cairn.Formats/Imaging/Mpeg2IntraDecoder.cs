using System.Globalization;

namespace Cairn.Formats.Imaging;

/// <summary>Reads a byte stream bit by bit, most significant bit first (MPEG video bit order).</summary>
public sealed class Mpeg2BitReader
{
    private readonly byte[] _data;
    private long _position;

    /// <summary>A reader over a copy of <paramref name="data"/>, positioned at its first bit.</summary>
    public Mpeg2BitReader(ReadOnlySpan<byte> data) => _data = data.ToArray();

    /// <summary>The stream's length in bits.</summary>
    public long Length => (long)_data.Length * 8;

    /// <summary>The position of the next bit to read.</summary>
    public long Position => _position;

    /// <summary>The next <paramref name="count"/> bits (1 to 32) without consuming them; bits past the end read as 0.</summary>
    public uint Peek(int count)
    {
        if (count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(count));
        long index = _position >> 3;
        int shift = (int)(_position & 7);
        ulong window = 0;
        for (int i = 0; i < 5; i++)
        {
            long at = index + i;
            window = (window << 8) | (at < _data.Length ? _data[at] : 0u);
        }
        return (uint)((window >> (40 - shift - count)) & ((1UL << count) - 1));
    }

    /// <summary>Reads <paramref name="count"/> bits (1 to 32).</summary>
    /// <exception cref="EndOfStreamException">The stream ends first.</exception>
    public uint Read(int count)
    {
        uint value = Peek(count);
        Skip(count);
        return value;
    }

    /// <summary>Reads one bit as a flag.</summary>
    public bool ReadFlag() => Read(1) != 0;

    /// <summary>Skips <paramref name="count"/> bits.</summary>
    /// <exception cref="EndOfStreamException">The stream ends first.</exception>
    public void Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_position + count > Length) throw new EndOfStreamException("The stream ends in the middle of a value.");
        _position += count;
    }

    /// <summary>
    /// Moves to the next byte boundary, then past the next start code prefix <c>00 00 01</c>, and reads the start
    /// code's value. Returns -1 (positioned at the end) when there is none.
    /// </summary>
    public int NextStartCode()
    {
        long index = (_position + 7) >> 3;
        for (long i = index; i + 3 < _data.Length; i++)
        {
            if (_data[i] != 0 || _data[i + 1] != 0 || _data[i + 2] != 1) continue;
            _position = (i + 4) * 8;
            return _data[i + 3];
        }
        _position = Length;
        return -1;
    }
}

/// <summary>A prefix-free code table decoded with one lookup.</summary>
internal sealed class Mpeg2Vlc
{
    private readonly int[] _table;
    private readonly int _bits;

    public Mpeg2Vlc(IEnumerable<(uint Code, int Length, int Value)> codes)
    {
        var list = codes.ToList();
        _bits = list.Max(c => c.Length);
        _table = new int[1 << _bits];
        foreach (var (code, length, value) in list)
        {
            if (value is < 0 or > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(codes));
            int first = (int)(code << (_bits - length)), count = 1 << (_bits - length);
            for (int i = 0; i < count; i++)
            {
                if (_table[first + i] != 0) throw new InvalidOperationException("The code table is not prefix-free.");
                _table[first + i] = (length << 24) | value;
            }
        }
    }

    /// <summary>The value of the next code, or -1 when the bits match no code (nothing is consumed then).</summary>
    public int Decode(Mpeg2BitReader reader)
    {
        int entry = _table[reader.Peek(_bits)];
        if (entry == 0) return -1;
        reader.Skip(entry >>> 24);
        return entry & 0xFFFFFF;
    }
}

/// <summary>
/// A decoded MPEG-2 picture: 8-bit luma and 4:2:0 chroma planes as coded (whole macroblocks), of which the top-left
/// <see cref="Width"/> x <see cref="Height"/> pixels are the picture.
/// </summary>
public sealed class Mpeg2Picture
{
    internal Mpeg2Picture(int width, int height, byte[] luma, int lumaStride, byte[] cb, byte[] cr, int chromaStride)
    {
        Width = width;
        Height = height;
        Luma = luma;
        LumaStride = lumaStride;
        Cb = cb;
        Cr = cr;
        ChromaStride = chromaStride;
    }

    /// <summary>Width from the sequence header.</summary>
    public int Width { get; }

    /// <summary>Height from the sequence header.</summary>
    public int Height { get; }

    /// <summary>The luma plane (Y), <see cref="LumaStride"/> bytes per row.</summary>
    public byte[] Luma { get; }

    /// <summary>Bytes per luma row (the coded width).</summary>
    public int LumaStride { get; }

    /// <summary>The blue-difference chroma plane, half size in both directions.</summary>
    public byte[] Cb { get; }

    /// <summary>The red-difference chroma plane, half size in both directions.</summary>
    public byte[] Cr { get; }

    /// <summary>Bytes per chroma row.</summary>
    public int ChromaStride { get; }

    /// <summary>The picture as BGRA32 (opaque).</summary>
    public BgraImage ToBgra()
    {
        var image = new BgraImage(Width, Height);
        CopyTo(image, 0, 0);
        return image;
    }

    /// <summary>
    /// Writes the picture into <paramref name="target"/> with its top-left corner at (<paramref name="x"/>,
    /// <paramref name="y"/>), clipped to the target. Chroma is repeated over each 2 x 2 block (nearest neighbour);
    /// colours are converted with the BT.601 matrix from limited (studio) range; alpha is opaque.
    /// </summary>
    public void CopyTo(BgraImage target, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(target);
        int w = Math.Min(Width, target.Width - x), h = Math.Min(Height, target.Height - y);
        var pixels = target.Pixels;
        for (int row = Math.Max(0, -y); row < h; row++)
        {
            int dst = ((y + row) * target.Width + x) * 4;
            int luma = row * LumaStride, chroma = (row >> 1) * ChromaStride;
            for (int col = Math.Max(0, -x); col < w; col++)
            {
                var (r, g, b) = Mpeg2IntraDecoder.ToRgb(Luma[luma + col], Cb[chroma + (col >> 1)], Cr[chroma + (col >> 1)]);
                int at = dst + col * 4;
                pixels[at] = b;
                pixels[at + 1] = g;
                pixels[at + 2] = r;
                pixels[at + 3] = 255;
            }
        }
    }
}

/// <summary>
/// Decodes the first picture of an MPEG-2 video elementary stream (ISO/IEC 13818-2) when it is what the PlayStation 2
/// texture packs hold: Main Profile, an intra-coded (I) frame picture, 4:2:0 chroma. Everything such a picture may use
/// is supported: loaded quantiser matrices (sequence header and quant matrix extension), intra DC precision 8 to 11
/// bits, both coefficient tables, escape coding, zigzag and alternate scan, linear and non-linear quantiser scale,
/// saturation and mismatch control, frame and field DCT, interlaced and progressive sequences. Anything else (P or B
/// pictures, field pictures, 4:2:2 or 4:4:4 chroma, scalable or MPEG-1 streams, concealment motion vectors) is
/// rejected with a message saying so. Malformed data gives an <see cref="ImageDecodeException"/>, never a hang or an
/// allocation larger than the picture the stream can actually hold.
/// </summary>
public static class Mpeg2IntraDecoder
{
    /// <summary>The widest picture decoded.</summary>
    public const int MaxWidth = 4096;

    /// <summary>The tallest picture decoded (taller ones need a slice position extension).</summary>
    public const int MaxHeight = 2800;

    private static readonly Mpeg2Vlc MbaTable = new(Mpeg2Tables.MacroblockAddressIncrement);
    private static readonly Mpeg2Vlc DcLumaTable = new(Mpeg2Tables.DcSizeLuminance);
    private static readonly Mpeg2Vlc DcChromaTable = new(Mpeg2Tables.DcSizeChrominance);
    private static readonly Mpeg2Vlc AcTableZero = CoefficientTable(tableOne: false);
    private static readonly Mpeg2Vlc AcTableOne = CoefficientTable(tableOne: true);
    private static readonly int[] ZigZag = [.. Mpeg2Tables.ZigZag];
    private static readonly int[] Alternate = [.. Mpeg2Tables.AlternateScan];
    private static readonly double[] Basis = BuildBasis();

    private const int EndOfBlock = 1 << 12, Escape = EndOfBlock + 1;

    private static Mpeg2Vlc CoefficientTable(bool tableOne)
    {
        var codes = Mpeg2Tables.RunLevelCodes(tableOne).Select(c => (c.Code, c.Length, c.Run * 64 + c.Level)).ToList();
        var eob = Mpeg2Tables.EndOfBlock(tableOne);
        codes.Add((eob.Code, eob.Length, EndOfBlock));
        codes.Add((Mpeg2Tables.EscapeCode, Mpeg2Tables.EscapeLength, Escape));
        return new Mpeg2Vlc(codes);
    }

    /// <summary>The kind of coefficient code read by <see cref="ReadCoefficient"/>.</summary>
    public enum CoefficientCode
    {
        /// <summary>A run/level pair (the sign included).</summary>
        RunLevel,
        /// <summary>The escape code (run and level follow as fixed-length fields, read too).</summary>
        Escape,
        /// <summary>End of block.</summary>
        EndOfBlock,
        /// <summary>No code matches the bits.</summary>
        Invalid,
    }

    /// <summary>Reads one <c>macroblock_address_increment</c> code (1..33, or 34 for the escape), or -1 when invalid.</summary>
    public static int ReadMacroblockIncrement(Mpeg2BitReader reader) => MbaTable.Decode(reader);

    /// <summary>Reads one <c>dct_dc_size</c> code, or -1 when invalid.</summary>
    public static int ReadDcSize(Mpeg2BitReader reader, bool luminance) => (luminance ? DcLumaTable : DcChromaTable).Decode(reader);

    /// <summary>Reads one coefficient code of an intra block (after the DC) from table B.15 (<paramref name="tableOne"/>) or B.14.</summary>
    public static CoefficientCode ReadCoefficient(Mpeg2BitReader reader, bool tableOne, out int run, out int level)
    {
        ArgumentNullException.ThrowIfNull(reader);
        run = level = 0;
        int value = (tableOne ? AcTableOne : AcTableZero).Decode(reader);
        switch (value)
        {
            case < 0: return CoefficientCode.Invalid;
            case EndOfBlock: return CoefficientCode.EndOfBlock;
            case Escape:
                run = (int)reader.Read(6);
                level = (int)reader.Read(12);
                if (level >= 2048) level -= 4096;
                return CoefficientCode.Escape;
            default:
                run = value / 64;
                level = reader.ReadFlag() ? -(value % 64) : value % 64;
                return CoefficientCode.RunLevel;
        }
    }

    /// <summary>
    /// The 8 x 8 inverse DCT of <paramref name="coefficients"/> (raster order, row = vertical frequency), computed in
    /// double precision with the separable orthonormal basis (the IEEE 1180 reference's accuracy).
    /// </summary>
    public static void InverseDct(ReadOnlySpan<int> coefficients, Span<double> output)
    {
        if (coefficients.Length < 64 || output.Length < 64) throw new ArgumentException("An 8 x 8 block needs 64 values.");
        Span<double> rows = stackalloc double[64];
        for (int v = 0; v < 8; v++)
        {
            bool empty = true;
            for (int u = 0; u < 8 && empty; u++) empty = coefficients[v * 8 + u] == 0;
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                if (!empty) for (int u = 0; u < 8; u++) sum += coefficients[v * 8 + u] * Basis[u * 8 + x];
                rows[v * 8 + x] = sum;
            }
        }
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                for (int v = 0; v < 8; v++) sum += Basis[v * 8 + y] * rows[v * 8 + x];
                output[y * 8 + x] = sum;
            }
        }
    }

    private static double[] BuildBasis()
    {
        var basis = new double[64];
        for (int k = 0; k < 8; k++)
            for (int n = 0; n < 8; n++)
                basis[k * 8 + n] = (k == 0 ? Math.Sqrt(0.125) : 0.5) * Math.Cos((2 * n + 1) * k * Math.PI / 16);
        return basis;
    }

    /// <summary>BT.601 limited-range Y'CbCr to RGB, rounded to nearest (ties to even) and clamped.</summary>
    public static (byte R, byte G, byte B) ToRgb(int y, int cb, int cr)
    {
        double luma = (y - 16) * 255.0 / 219.0, u = cb - 128, v = cr - 128;
        return (Clamp(luma + 1.596 * v), Clamp(luma - 0.392 * u - 0.813 * v), Clamp(luma + 2.017 * u));
    }

    private static byte Clamp(double value)
    {
        double rounded = Math.Round(value, MidpointRounding.ToEven);
        return rounded <= 0 ? (byte)0 : rounded >= 255 ? (byte)255 : (byte)rounded;
    }

    /// <summary>Decodes the stream's first picture.</summary>
    /// <param name="stream">An MPEG-2 video elementary stream starting with a sequence header.</param>
    /// <param name="name">A name for messages.</param>
    /// <exception cref="ImageDecodeException">Malformed, truncated, or outside what this decoder supports.</exception>
    public static Mpeg2Picture Decode(ReadOnlySpan<byte> stream, string? name = null)
    {
        var decoder = new Decoder(stream, name ?? "MPEG-2 stream");
        try
        {
            return decoder.Run();
        }
        catch (EndOfStreamException)
        {
            throw decoder.Fail("the stream ends in the middle of the picture");
        }
    }

    private sealed class Decoder(ReadOnlySpan<byte> stream, string name)
    {
        private readonly Mpeg2BitReader _r = new(stream);
        private readonly int[] _intraMatrix = [.. Mpeg2Tables.DefaultIntraMatrix];
        private int _width, _height;
        private bool _haveSequence, _haveExtension, _progressiveSequence;
        private bool _havePicture, _haveCodingExtension;
        private int _dcPrecision;
        private bool _framePredFrameDct, _nonLinearQ, _tableOne, _alternateScan;
        private byte[]? _luma, _cb, _cr;
        private int _mbWidth, _mbHeight, _decodedCount;
        private bool[] _decoded = [];

        public ImageDecodeException Fail(string why) => new($"'{name}' cannot be decoded as MPEG-2 video: {why}.");

        private ImageDecodeException Unsupported(string what) =>
            new($"'{name}' uses {what}, which Cairn does not decode (it decodes intra-coded 4:2:0 MPEG-2 frame pictures).");

        public Mpeg2Picture Run()
        {
            int code = _r.NextStartCode();
            if (code != 0xB3) throw Fail("it does not start with a sequence header");
            bool done = false;
            while (code >= 0 && !done)
            {
                switch (code)
                {
                    case 0xB3:
                        if (_luma is not null) { done = true; break; }
                        SequenceHeader();
                        break;
                    case 0xB5: Extension(); break;
                    case 0xB2 or 0xB8: break; // user data, group of pictures: nothing needed
                    case 0xB7: done = true; break;
                    case 0x00:
                        if (_luma is not null) { done = true; break; } // only the first picture is decoded
                        PictureHeader();
                        break;
                    case >= 0x01 and <= 0xAF: Slice(code); break;
                    case 0xB4: throw Fail("it contains a sequence error code");
                    case >= 0xB9: throw Fail("it is a system (program or transport) stream, not a video elementary stream");
                    default: throw Fail(string.Create(CultureInfo.InvariantCulture, $"it uses the reserved start code 0x{code:X2}"));
                }
                if (!done) code = _r.NextStartCode();
            }
            if (_luma is null || _cb is null || _cr is null) throw Fail("it holds no picture data");
            int total = _mbWidth * _mbHeight;
            if (_decodedCount < total)
                throw Fail(string.Create(CultureInfo.InvariantCulture, $"the picture is incomplete ({_decodedCount:N0} of {total:N0} macroblocks)"));
            return new Mpeg2Picture(_width, _height, _luma, _mbWidth * 16, _cb, _cr, _mbWidth * 8);
        }

        private void SequenceHeader()
        {
            int width = (int)_r.Read(12), height = (int)_r.Read(12);
            _r.Skip(4 + 4 + 18 + 1 + 10 + 1); // aspect ratio, frame rate, bit rate, marker, VBV buffer, constrained
            if (_r.ReadFlag()) for (int i = 0; i < 64; i++) _intraMatrix[ZigZag[i]] = (int)_r.Read(8);
            else Mpeg2Tables.DefaultIntraMatrix.ToArray().CopyTo(_intraMatrix, 0);
            if (_r.ReadFlag()) _r.Skip(64 * 8); // non-intra matrix: not used by intra pictures
            if (_intraMatrix.Any(w => w == 0)) throw Fail("its intra quantiser matrix holds a 0");
            _width = width;
            _height = height;
            _haveSequence = true;
            _haveExtension = false;
        }

        private void Extension()
        {
            int id = (int)_r.Read(4);
            switch (id)
            {
                case 1: // sequence extension
                {
                    if (!_haveSequence) throw Fail("a sequence extension comes before the sequence header");
                    _r.Skip(8); // profile and level
                    _progressiveSequence = _r.ReadFlag();
                    int chroma = (int)_r.Read(2);
                    if (chroma != 1) throw Unsupported(chroma == 2 ? "4:2:2 chroma" : chroma == 3 ? "4:4:4 chroma" : "a reserved chroma format");
                    _width |= (int)_r.Read(2) << 12;
                    _height |= (int)_r.Read(2) << 12;
                    if (_width == 0 || _height == 0) throw Fail("its picture size is 0");
                    if (_width > MaxWidth || _height > MaxHeight)
                        throw Unsupported(string.Create(CultureInfo.InvariantCulture, $"a {_width} x {_height} picture (the limit is {MaxWidth} x {MaxHeight})"));
                    _haveExtension = true;
                    break;
                }
                case 3: // quant matrix extension
                    if (_r.ReadFlag())
                    {
                        for (int i = 0; i < 64; i++) _intraMatrix[ZigZag[i]] = (int)_r.Read(8);
                        if (_intraMatrix.Any(w => w == 0)) throw Fail("its intra quantiser matrix holds a 0");
                    }
                    // the non-intra and (4:2:2 / 4:4:4 only) chroma matrices are not used here
                    break;
                case 5: throw Unsupported("a sequence scalable extension");
                case 8: // picture coding extension
                {
                    if (!_havePicture) throw Fail("a picture coding extension comes before the picture header");
                    _r.Skip(16); // f_codes: no motion vectors in an intra picture
                    _dcPrecision = (int)_r.Read(2);
                    int structure = (int)_r.Read(2);
                    if (structure != 3) throw Unsupported(structure == 0 ? "a reserved picture structure" : "field pictures");
                    _r.Skip(1); // top_field_first
                    _framePredFrameDct = _r.ReadFlag();
                    if (_r.ReadFlag()) throw Unsupported("concealment motion vectors");
                    _nonLinearQ = _r.ReadFlag();
                    _tableOne = _r.ReadFlag();
                    _alternateScan = _r.ReadFlag();
                    _haveCodingExtension = true;
                    break;
                }
                case 9: throw Unsupported("a picture spatial scalable extension");
                case 10: throw Unsupported("a picture temporal scalable extension");
                default: break; // sequence display, copyright, picture display, ...: not needed
            }
        }

        private void PictureHeader()
        {
            if (!_haveSequence) throw Fail("a picture comes before the sequence header");
            if (!_haveExtension) throw Unsupported("MPEG-1 coding (the stream has no sequence extension)");
            _r.Skip(10); // temporal reference
            int type = (int)_r.Read(3);
            if (type != 1)
                throw Unsupported(type switch { 2 => "P pictures", 3 => "B pictures", 4 => "D pictures", _ => "an invalid picture type" });
            _r.Skip(16); // VBV delay
            while (_r.ReadFlag()) _r.Skip(8); // extra information
            _havePicture = true;
            _haveCodingExtension = false;
        }

        private void Allocate()
        {
            _mbWidth = (_width + 15) / 16;
            _mbHeight = _progressiveSequence ? (_height + 15) / 16 : 2 * ((_height + 31) / 32);
            long macroblocks = (long)_mbWidth * _mbHeight;
            // Each intra macroblock needs at least 30 bits (the shortest codes): a stream too short for the picture
            // it declares is refused before its planes are allocated.
            if (macroblocks * 30 > _r.Length) throw Fail(string.Create(CultureInfo.InvariantCulture, $"it is too short for a {_width} x {_height} picture"));
            DecodeLimits.EnsureWithinBudget(_width, _height, name);
            _luma = new byte[_mbWidth * 16 * _mbHeight * 16];
            _cb = new byte[_mbWidth * 8 * _mbHeight * 8];
            _cr = new byte[_cb.Length];
            _decoded = new bool[macroblocks];
        }

        private void Slice(int code)
        {
            if (!_havePicture || !_haveCodingExtension) throw Fail("a slice comes before the picture header and its coding extension");
            if (_luma is null) Allocate();
            int row = code - 1;
            if (row >= _mbHeight) throw Fail(string.Create(CultureInfo.InvariantCulture, $"slice row {row + 1} is below the picture"));
            int quantiserScaleCode = ReadQuantiserScaleCode();
            if (_r.ReadFlag()) // intra_slice_flag, intra_slice, reserved bits, then extra information
            {
                _r.Skip(8);
                while (_r.ReadFlag()) _r.Skip(8);
            }
            int dcReset = 1 << (7 + _dcPrecision);
            Span<int> predictors = [dcReset, dcReset, dcReset];
            int address = -1;
            int rowEnd = (row + 1) * _mbWidth;
            do
            {
                int increment = 0;
                while (true)
                {
                    int value = MbaTable.Decode(_r);
                    if (value < 0) throw Fail("it holds an invalid macroblock address code");
                    if (value == Mpeg2Tables.MacroblockEscape) { increment += 33; continue; }
                    increment += value;
                    break;
                }
                if (address < 0) address = row * _mbWidth + increment - 1;
                else if (increment != 1) throw Fail("it skips macroblocks in an intra picture");
                else address++;
                if (address >= rowEnd) throw Fail("a slice runs past the end of its row");

                bool quant;
                if (_r.ReadFlag()) quant = false;
                else if (_r.ReadFlag()) quant = true;
                else throw Fail("it holds a macroblock type an intra picture cannot have");
                bool fieldDct = !_framePredFrameDct && _r.ReadFlag();
                if (quant) quantiserScaleCode = ReadQuantiserScaleCode();
                int scale = _nonLinearQ ? Mpeg2Tables.NonLinearQuantiserScale[quantiserScaleCode] : 2 * quantiserScaleCode;
                Macroblock(address % _mbWidth, address / _mbWidth, fieldDct, scale, predictors);
                if (!_decoded[address]) { _decoded[address] = true; _decodedCount++; }
            }
            while (_r.Peek(23) != 0);
        }

        private int ReadQuantiserScaleCode()
        {
            int code = (int)_r.Read(5);
            if (code == 0) throw Fail("it uses quantiser scale code 0");
            return code;
        }

        private void Macroblock(int mbX, int mbY, bool fieldDct, int scale, Span<int> predictors)
        {
            Span<int> coefficients = stackalloc int[64];
            Span<double> samples = stackalloc double[64];
            int lumaStride = _mbWidth * 16, chromaStride = _mbWidth * 8;
            for (int block = 0; block < 6; block++)
            {
                int component = block < 4 ? 0 : block - 3;
                Block(component, scale, ref predictors[component], coefficients);
                InverseDct(coefficients, samples);
                byte[] plane;
                int at, step;
                if (component == 0)
                {
                    int x = mbX * 16 + (block & 1) * 8;
                    int y = fieldDct ? mbY * 16 + (block >> 1) : mbY * 16 + (block >> 1) * 8;
                    plane = _luma!;
                    at = y * lumaStride + x;
                    step = fieldDct ? lumaStride * 2 : lumaStride;
                }
                else
                {
                    plane = component == 1 ? _cb! : _cr!;
                    at = mbY * 8 * chromaStride + mbX * 8;
                    step = chromaStride;
                }
                for (int i = 0; i < 8; i++, at += step)
                    for (int j = 0; j < 8; j++)
                        plane[at + j] = Clamp(samples[i * 8 + j]);
            }
        }

        private void Block(int component, int scale, ref int predictor, Span<int> f)
        {
            f.Clear();
            int size = ReadDcSize(_r, component == 0);
            if (size < 0) throw Fail("it holds an invalid DC size code");
            if (size > 0)
            {
                int bits = (int)_r.Read(size);
                predictor += bits >= 1 << (size - 1) ? bits : bits - ((1 << size) - 1);
            }
            f[0] = Saturate(predictor * (8 >> _dcPrecision));
            long sum = f[0];
            var scan = _alternateScan ? Alternate : ZigZag;
            int n = 1;
            while (true)
            {
                var kind = ReadCoefficient(_r, _tableOne, out int run, out int level);
                if (kind == CoefficientCode.EndOfBlock) break;
                if (kind == CoefficientCode.Invalid) throw Fail("it holds an invalid coefficient code");
                if (level == 0) throw Fail("it holds an escape-coded coefficient of level 0");
                n += run;
                if (n > 63) throw Fail("a block holds more than 64 coefficients");
                int position = scan[n];
                int magnitude = 2 * Math.Abs(level) * _intraMatrix[position] * scale / 32;
                int value = Saturate(level < 0 ? -magnitude : magnitude);
                f[position] = value;
                sum += value;
                n++;
            }
            // mismatch control: make the sum of the coefficients odd through the last one
            if ((sum & 1) == 0) f[63] += (f[63] & 1) != 0 ? -1 : 1;
        }

        private static int Saturate(int value) => Math.Clamp(value, -2048, 2047);
    }
}
