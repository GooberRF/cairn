using System.Buffers.Binary;
using Cairn.Formats.Imaging;

namespace Cairn.Vpp.Ps2;

/// <summary>How <see cref="SyntheticMpeg2"/> codes a picture (each switch exercises one decoder path).</summary>
/// <param name="TableOne">intra_vlc_format: table B.15 (true, as the PS2 files) or B.14.</param>
/// <param name="AlternateScan">alternate_scan instead of zigzag.</param>
/// <param name="NonLinearQuantiser">q_scale_type = 1.</param>
/// <param name="DcPrecision">intra_dc_precision 0..3 (8..11 bits).</param>
/// <param name="FieldDct">Use field DCT for every other macroblock (frame_pred_frame_dct = 0); false codes frame DCT only.</param>
/// <param name="QuantiserScaleCode">The slices' quantiser_scale_code (1..31).</param>
/// <param name="ChangeQuantiser">Code every third macroblock with its own quantiser_scale_code (macroblock_quant).</param>
/// <param name="Matrix">The intra matrix (raster order) to code with; null = the default matrix.</param>
/// <param name="MatrixInExtension">Send <paramref name="Matrix"/> in a quant matrix extension instead of the sequence header.</param>
/// <param name="ProgressiveSequence">progressive_sequence = 1 (macroblock rows = height / 16).</param>
/// <param name="DcOnly">Code no AC coefficients (each block a flat colour).</param>
internal sealed record Mpeg2EncodeOptions(
    bool TableOne = true,
    bool AlternateScan = false,
    bool NonLinearQuantiser = false,
    int DcPrecision = 1,
    bool FieldDct = true,
    int QuantiserScaleCode = 1,
    bool ChangeQuantiser = false,
    int[]? Matrix = null,
    bool MatrixInExtension = false,
    bool ProgressiveSequence = false,
    bool DcOnly = false);

/// <summary>
/// A small MPEG-2 intra-only encoder for the tests and self-tests (no game files): codes an image as one I picture in
/// the stream layout of the PlayStation 2 texture packs (sequence header with the intra matrix, sequence extension,
/// GOP, picture header, picture coding extension, one slice per macroblock row, end code), and builds PEG format-2
/// data blocks out of such tiles.
/// </summary>
internal static class SyntheticMpeg2
{
    /// <summary>Codes <paramref name="image"/> (its colours; alpha ignored) as one MPEG-2 stream.</summary>
    public static byte[] Encode(BgraImage image, Mpeg2EncodeOptions? options = null) =>
        Encode(image, 0, 0, image.Width, image.Height, options ?? new());

    /// <summary>
    /// A PEG format-2 data block for <paramref name="image"/>: tiles of at most 256 x 256 (sizes rounded up to 16,
    /// edge pixels repeated), the unused header bytes filled with <paramref name="junk"/> (which may contain start
    /// code patterns, as some PS2 files do).
    /// </summary>
    public static byte[] PegData(BgraImage image, Mpeg2EncodeOptions? options = null, byte junk = 0)
    {
        options ??= new();
        var data = new List<byte>(new byte[16]);
        bool first = true;
        for (int y = 0; y < image.Height; y += 256)
        {
            for (int x = 0; x < image.Width; x += 256)
            {
                int w = (Math.Min(256, image.Width - x) + 15) & ~15, h = (Math.Min(256, image.Height - y) + 15) & ~15;
                byte[] tile = Encode(image, x, y, w, h, options);
                int at;
                if (first) at = 4;
                else
                {
                    while (data.Count % 16 != 0) data.Add(junk);
                    at = data.Count;
                    data.AddRange(new byte[16]);
                    // junk after the length, including a fake start code
                    for (int i = 4; i < 16; i++) data[at + i] = i is >= 8 and <= 11 ? (byte)(i == 10 ? 1 : 0) : junk;
                }
                var length = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)tile.Length);
                for (int i = 0; i < 4; i++) data[at + i] = length[i];
                if (first) for (int i = 8; i < 16; i++) data[i] = junk;
                data.AddRange(tile);
                first = false;
            }
        }
        while (data.Count % 16 != 0) data.Add(junk);
        data.AddRange(Enumerable.Repeat(junk, 16));
        var bytes = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(bytes.Length - 12));
        return bytes;
    }

    private static byte[] Encode(BgraImage image, int left, int top, int width, int height, Mpeg2EncodeOptions o)
    {
        int mbWidth = (width + 15) / 16;
        int mbHeight = o.ProgressiveSequence ? (height + 15) / 16 : 2 * ((height + 31) / 32);
        int codedW = mbWidth * 16, codedH = mbHeight * 16;
        // planes (edge pixels repeated past the image), chroma averaged over 2 x 2
        var lumaPlane = new double[codedW * codedH];
        var cbFull = new double[codedW * codedH];
        var crFull = new double[codedW * codedH];
        for (int y = 0; y < codedH; y++)
        {
            for (int x = 0; x < codedW; x++)
            {
                int sx = Math.Min(left + x, image.Width - 1), sy = Math.Min(top + y, image.Height - 1);
                int at = (sy * image.Width + sx) * 4;
                double b = image.Pixels[at], g = image.Pixels[at + 1], r = image.Pixels[at + 2];
                lumaPlane[y * codedW + x] = 16 + (65.481 * r + 128.553 * g + 24.966 * b) / 255;
                cbFull[y * codedW + x] = 128 + (-37.797 * r - 74.203 * g + 112.0 * b) / 255;
                crFull[y * codedW + x] = 128 + (112.0 * r - 93.786 * g - 18.214 * b) / 255;
            }
        }
        int chromaW = codedW / 2;
        var cb = new double[chromaW * (codedH / 2)];
        var cr = new double[cb.Length];
        for (int y = 0; y < codedH / 2; y++)
            for (int x = 0; x < chromaW; x++)
            {
                int a = 2 * y * codedW + 2 * x;
                cb[y * chromaW + x] = (cbFull[a] + cbFull[a + 1] + cbFull[a + codedW] + cbFull[a + codedW + 1]) / 4;
                cr[y * chromaW + x] = (crFull[a] + crFull[a + 1] + crFull[a + codedW] + crFull[a + codedW + 1]) / 4;
            }

        int[] matrix = o.Matrix ?? [.. Mpeg2Tables.DefaultIntraMatrix];
        var zigzag = Mpeg2Tables.ZigZag;
        var w = new BitWriter();
        // sequence header: size, aspect 1, 30 fps, bit rate 12500, marker, VBV 112, not constrained, matrices
        w.StartCode(0xB3);
        w.Put((uint)width, 12); w.Put((uint)height, 12); w.Put(1, 4); w.Put(5, 4); w.Put(12500, 18); w.Put(1, 1); w.Put(112, 10); w.Put(0, 1);
        bool inHeader = o.Matrix is null || !o.MatrixInExtension;
        w.Put(inHeader ? 1u : 0u, 1);
        if (inHeader) for (int i = 0; i < 64; i++) w.Put((uint)matrix[zigzag[i]], 8);
        w.Put(1, 1);
        for (int i = 0; i < 64; i++) w.Put(16, 8); // a non-intra matrix (unused by intra pictures)
        // sequence extension: MP@ML, 4:2:0
        w.StartCode(0xB5);
        w.Put(1, 4); w.Put(0x48, 8); w.Put(o.ProgressiveSequence ? 1u : 0u, 1); w.Put(1, 2); w.Put(0, 2); w.Put(0, 2); w.Put(0, 12); w.Put(1, 1); w.Put(0, 8); w.Put(0, 1); w.Put(0, 2); w.Put(0, 5);
        // sequence display extension (ignored by the decoder)
        w.StartCode(0xB5);
        w.Put(2, 4); w.Put(2, 3); w.Put(0, 1); w.Put((uint)width, 14); w.Put(1, 1); w.Put((uint)height, 14);
        if (!inHeader)
        {
            w.StartCode(0xB5);
            w.Put(3, 4); w.Put(1, 1);
            for (int i = 0; i < 64; i++) w.Put((uint)matrix[zigzag[i]], 8);
            w.Put(0, 1); w.Put(0, 1); w.Put(0, 1);
        }
        // GOP: time code 0 (with its marker bit), closed
        w.StartCode(0xB8);
        w.Put(0, 1); w.Put(0, 5); w.Put(0, 6); w.Put(1, 1); w.Put(0, 6); w.Put(0, 6); w.Put(1, 1); w.Put(0, 1);
        // picture header: I picture
        w.StartCode(0x00);
        w.Put(0, 10); w.Put(1, 3); w.Put(0xFFFF, 16); w.Put(0, 1);
        // picture coding extension
        bool frameDctOnly = !o.FieldDct;
        w.StartCode(0xB5);
        w.Put(8, 4); w.Put(0xFFFF, 16); w.Put((uint)o.DcPrecision, 2); w.Put(3, 2); w.Put(0, 1); w.Put(frameDctOnly ? 1u : 0u, 1); w.Put(0, 1);
        w.Put(o.NonLinearQuantiser ? 1u : 0u, 1); w.Put(o.TableOne ? 1u : 0u, 1); w.Put(o.AlternateScan ? 1u : 0u, 1);
        w.Put(0, 1); w.Put(o.ProgressiveSequence ? 1u : 0u, 1); w.Put(o.ProgressiveSequence ? 1u : 0u, 1); w.Put(0, 1);

        var scan = o.AlternateScan ? Mpeg2Tables.AlternateScan : Mpeg2Tables.ZigZag;
        var codes = Mpeg2Tables.RunLevelCodes(o.TableOne).ToDictionary(c => (c.Run, c.Level), c => (c.Code, c.Length));
        var eob = Mpeg2Tables.EndOfBlock(o.TableOne);
        int dcMult = 8 >> o.DcPrecision, dcMax = (1 << (8 + o.DcPrecision)) - 1;
        var block = new double[64];
        for (int row = 0; row < mbHeight; row++)
        {
            w.StartCode((byte)(row + 1));
            w.Put((uint)o.QuantiserScaleCode, 5);
            w.Put(0, 1); // extra_bit_slice
            int[] predictors = [1 << (7 + o.DcPrecision), 1 << (7 + o.DcPrecision), 1 << (7 + o.DcPrecision)];
            int code = o.QuantiserScaleCode; // a macroblock's own code holds for the rest of the slice
            for (int col = 0; col < mbWidth; col++)
            {
                w.Put(1, 1); // macroblock_address_increment 1
                int index = row * mbWidth + col;
                bool quant = o.ChangeQuantiser && index % 3 == 1;
                if (quant) code = 1 + (index * 7 % 20);
                if (quant) w.Put(0b01, 2); else w.Put(1, 1); // macroblock_type: intra (+ quant)
                bool field = !frameDctOnly && (row + col) % 2 == 1;
                if (!frameDctOnly) w.Put(field ? 1u : 0u, 1);
                if (quant) w.Put((uint)code, 5);
                int scale = o.NonLinearQuantiser ? Mpeg2Tables.NonLinearQuantiserScale[code] : 2 * code;
                for (int b = 0; b < 6; b++)
                {
                    int component = b < 4 ? 0 : b - 3;
                    for (int i = 0; i < 8; i++)
                        for (int j = 0; j < 8; j++)
                        {
                            if (component == 0)
                            {
                                int x = col * 16 + (b & 1) * 8 + j;
                                int y = field ? row * 16 + (b >> 1) + 2 * i : row * 16 + (b >> 1) * 8 + i;
                                block[i * 8 + j] = lumaPlane[y * codedW + x];
                            }
                            else block[i * 8 + j] = (component == 1 ? cb : cr)[(row * 8 + i) * chromaW + col * 8 + j];
                        }
                    var f = ForwardDct(block);
                    // DC
                    int dc = Math.Clamp((int)Math.Round(f[0] / dcMult), 0, dcMax);
                    int diff = dc - predictors[component];
                    predictors[component] = dc;
                    int size = 0;
                    for (int m = Math.Abs(diff); m > 0; m >>= 1) size++;
                    var table = component == 0 ? Mpeg2Tables.DcSizeLuminance : Mpeg2Tables.DcSizeChrominance;
                    var sizeCode = table.First(c => c.Value == size);
                    w.Put(sizeCode.Code, sizeCode.Length);
                    if (size > 0) w.Put((uint)(diff > 0 ? diff : diff + (1 << size) - 1), size);
                    // AC
                    int run = 0;
                    for (int n = 1; n < 64 && !o.DcOnly; n++)
                    {
                        int position = scan[n];
                        int level = (int)Math.Round(f[position] * 16.0 / (matrix[position] * scale));
                        level = Math.Clamp(level, -2047, 2047);
                        if (level == 0) { run++; continue; }
                        if (codes.TryGetValue((run, Math.Abs(level)), out var c))
                        {
                            w.Put(c.Code, c.Length);
                            w.Put(level < 0 ? 1u : 0u, 1);
                        }
                        else
                        {
                            w.Put(Mpeg2Tables.EscapeCode, Mpeg2Tables.EscapeLength);
                            w.Put((uint)run, 6);
                            w.Put((uint)(level & 0xFFF), 12);
                        }
                        run = 0;
                    }
                    w.Put(eob.Code, eob.Length);
                }
            }
        }
        w.StartCode(0xB7);
        return w.ToArray();
    }

    /// <summary>The orthonormal 8 x 8 forward DCT (raster order, row = vertical frequency).</summary>
    internal static double[] ForwardDct(double[] samples)
    {
        var rows = new double[64];
        for (int y = 0; y < 8; y++)
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int x = 0; x < 8; x++) sum += samples[y * 8 + x] * Basis[u * 8 + x];
                rows[y * 8 + u] = sum;
            }
        var f = new double[64];
        for (int v = 0; v < 8; v++)
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++) sum += rows[y * 8 + u] * Basis[v * 8 + y];
                f[v * 8 + u] = sum;
            }
        return f;
    }

    // Basis[k * 8 + n] = c(k) cos((2n + 1) k pi / 16), orthonormal
    private static readonly double[] Basis = [.. Enumerable.Range(0, 64).Select(i => (i / 8 == 0 ? Math.Sqrt(0.125) : 0.5) * Math.Cos((2 * (i % 8) + 1) * (i / 8) * Math.PI / 16))];

    /// <summary>Writes bits most significant first; start codes are byte aligned with zero bits first.</summary>
    internal sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _current, _used;

        public void Put(uint value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _current = (_current << 1) | (int)((value >> i) & 1);
                if (++_used == 8) { _bytes.Add((byte)_current); _current = 0; _used = 0; }
            }
        }

        public void Align()
        {
            while (_used != 0) Put(0, 1);
        }

        public void StartCode(byte code)
        {
            Align();
            _bytes.AddRange([0, 0, 1, code]);
        }

        public byte[] ToArray()
        {
            Align();
            return [.. _bytes];
        }
    }
}
