namespace Cairn.Formats.Imaging;

/// <summary>
/// The fixed tables of MPEG-2 video (ISO/IEC 13818-2) that intra-coded pictures use: scans, the default intra
/// quantiser matrix, the non-linear quantiser scale and the variable-length codes of tables B.1, B.2, B.12, B.13, B.14
/// and B.15 (codes are given most significant bit first, without the sign bit that follows a run/level code).
/// </summary>
public static class Mpeg2Tables
{
    /// <summary>Zigzag scan: scan position to raster position (row * 8 + column).</summary>
    public static IReadOnlyList<int> ZigZag { get; } =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14,
        21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53,
        60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>Alternate scan (used when <c>alternate_scan</c> is set): scan position to raster position.</summary>
    public static IReadOnlyList<int> AlternateScan { get; } =
    [
        0, 8, 16, 24, 1, 9, 2, 10, 17, 25, 32, 40, 48, 56, 57, 49, 41, 33, 26, 18, 3, 11, 4, 12, 19, 27, 34, 42,
        50, 58, 35, 43, 51, 59, 20, 28, 5, 13, 6, 14, 21, 29, 36, 44, 52, 60, 37, 45, 53, 61, 22, 30, 7, 15, 23,
        31, 38, 46, 54, 62, 39, 47, 55, 63,
    ];

    /// <summary>The default intra quantiser matrix in raster order.</summary>
    public static IReadOnlyList<int> DefaultIntraMatrix { get; } =
    [
        8, 16, 19, 22, 26, 27, 29, 34, 16, 16, 22, 24, 27, 29, 34, 37, 19, 22, 26, 27, 29, 34, 34, 38,
        22, 22, 26, 27, 29, 34, 37, 40, 22, 26, 27, 29, 32, 35, 40, 48, 26, 27, 29, 32, 35, 40, 48, 58,
        26, 27, 29, 34, 38, 46, 56, 69, 27, 29, 35, 38, 46, 56, 69, 83,
    ];

    /// <summary><c>quantiser_scale</c> for each <c>quantiser_scale_code</c> when <c>q_scale_type</c> = 1 (index 0 is forbidden).</summary>
    public static IReadOnlyList<int> NonLinearQuantiserScale { get; } =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80, 88, 96, 104, 112,
    ];

    /// <summary>The value <see cref="MacroblockAddressIncrement"/> gives the escape code (adds 33 to the increment).</summary>
    public const int MacroblockEscape = 34;

    /// <summary>Table B.1: <c>macroblock_address_increment</c> 1..33, and the escape (<see cref="MacroblockEscape"/>).</summary>
    public static IReadOnlyList<(uint Code, int Length, int Value)> MacroblockAddressIncrement { get; } =
    [
        (0b1, 1, 1), (0b011, 3, 2), (0b010, 3, 3), (0b0011, 4, 4), (0b0010, 4, 5), (0b00011, 5, 6), (0b00010, 5, 7),
        (0b0000111, 7, 8), (0b0000110, 7, 9), (0b00001011, 8, 10), (0b00001010, 8, 11), (0b00001001, 8, 12),
        (0b00001000, 8, 13), (0b00000111, 8, 14), (0b00000110, 8, 15), (0b0000010111, 10, 16), (0b0000010110, 10, 17),
        (0b0000010101, 10, 18), (0b0000010100, 10, 19), (0b0000010011, 10, 20), (0b0000010010, 10, 21),
        (0b00000100011, 11, 22), (0b00000100010, 11, 23), (0b00000100001, 11, 24), (0b00000100000, 11, 25),
        (0b00000011111, 11, 26), (0b00000011110, 11, 27), (0b00000011101, 11, 28), (0b00000011100, 11, 29),
        (0b00000011011, 11, 30), (0b00000011010, 11, 31), (0b00000011001, 11, 32), (0b00000011000, 11, 33),
        (0b00000001000, 11, MacroblockEscape),
    ];

    /// <summary>Table B.12: <c>dct_dc_size_luminance</c> 0..11.</summary>
    public static IReadOnlyList<(uint Code, int Length, int Value)> DcSizeLuminance { get; } =
    [
        (0b100, 3, 0), (0b00, 2, 1), (0b01, 2, 2), (0b101, 3, 3), (0b110, 3, 4), (0b1110, 4, 5), (0b11110, 5, 6),
        (0b111110, 6, 7), (0b1111110, 7, 8), (0b11111110, 8, 9), (0b111111110, 9, 10), (0b111111111, 9, 11),
    ];

    /// <summary>Table B.13: <c>dct_dc_size_chrominance</c> 0..11.</summary>
    public static IReadOnlyList<(uint Code, int Length, int Value)> DcSizeChrominance { get; } =
    [
        (0b00, 2, 0), (0b01, 2, 1), (0b10, 2, 2), (0b110, 3, 3), (0b1110, 4, 4), (0b11110, 5, 5), (0b111110, 6, 6),
        (0b1111110, 7, 7), (0b11111110, 8, 8), (0b111111110, 9, 9), (0b1111111110, 10, 10), (0b1111111111, 10, 11),
    ];

    /// <summary>The escape code of both coefficient tables: <c>000001</c>, then a 6-bit run and a 12-bit signed level.</summary>
    public const uint EscapeCode = 0b000001;

    /// <summary>Length of <see cref="EscapeCode"/>.</summary>
    public const int EscapeLength = 6;

    // Run and level of the 111 run/level codes, in the order of the two code lists below.
    private static readonly int[] Runs =
    [
        .. Enumerable.Repeat(0, 40), .. Enumerable.Repeat(1, 18), .. Enumerable.Repeat(2, 5), .. Enumerable.Repeat(3, 4),
        .. Enumerable.Repeat(4, 3), .. Enumerable.Repeat(5, 3), .. Enumerable.Repeat(6, 3),
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, .. Enumerable.Range(17, 15),
    ];

    private static readonly int[] Levels =
    [
        .. Enumerable.Range(1, 40), .. Enumerable.Range(1, 18), 1, 2, 3, 4, 5, 1, 2, 3, 4, 1, 2, 3, 1, 2, 3, 1, 2, 3,
        1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, .. Enumerable.Repeat(1, 15),
    ];

    // Table B.15 (intra_vlc_format = 1) codes, as (code, length).
    private static readonly (uint Code, int Length)[] TableOneCodes =
    [
        (0x02, 2), (0x06, 3), (0x07, 4), (0x1c, 5), (0x1d, 5), (0x05, 6), (0x04, 6), (0x7b, 7), (0x7c, 7), (0x23, 8),
        (0x22, 8), (0xfa, 8), (0xfb, 8), (0xfe, 8), (0xff, 8), (0x1f, 14), (0x1e, 14), (0x1d, 14), (0x1c, 14),
        (0x1b, 14), (0x1a, 14), (0x19, 14), (0x18, 14), (0x17, 14), (0x16, 14), (0x15, 14), (0x14, 14), (0x13, 14),
        (0x12, 14), (0x11, 14), (0x10, 14), (0x18, 15), (0x17, 15), (0x16, 15), (0x15, 15), (0x14, 15), (0x13, 15),
        (0x12, 15), (0x11, 15), (0x10, 15), (0x02, 3), (0x06, 5), (0x79, 7), (0x27, 8), (0x20, 8), (0x16, 13),
        (0x15, 13), (0x1f, 15), (0x1e, 15), (0x1d, 15), (0x1c, 15), (0x1b, 15), (0x1a, 15), (0x19, 15), (0x13, 16),
        (0x12, 16), (0x11, 16), (0x10, 16), (0x05, 5), (0x07, 7), (0xfc, 8), (0x0c, 10), (0x14, 13), (0x07, 5),
        (0x26, 8), (0x1c, 12), (0x13, 13), (0x06, 6), (0xfd, 8), (0x12, 12), (0x07, 6), (0x04, 9), (0x12, 13),
        (0x06, 7), (0x1e, 12), (0x14, 16), (0x04, 7), (0x15, 12), (0x05, 7), (0x11, 12), (0x78, 7), (0x11, 13),
        (0x7a, 7), (0x10, 13), (0x21, 8), (0x1a, 16), (0x25, 8), (0x19, 16), (0x24, 8), (0x18, 16), (0x05, 9),
        (0x17, 16), (0x07, 9), (0x16, 16), (0x0d, 10), (0x15, 16), (0x1f, 12), (0x1a, 12), (0x19, 12), (0x17, 12),
        (0x16, 12), (0x1f, 13), (0x1e, 13), (0x1d, 13), (0x1c, 13), (0x1b, 13), (0x1f, 16), (0x1e, 16), (0x1d, 16),
        (0x1c, 16), (0x1b, 16),
    ];

    // Table B.14 (intra_vlc_format = 0) codes for every coefficient after the DC of an intra block.
    private static readonly (uint Code, int Length)[] TableZeroCodes =
    [
        (0x03, 2), (0x04, 4), (0x05, 5), (0x06, 7), (0x26, 8), (0x21, 8), (0x0a, 10), (0x1d, 12), (0x18, 12),
        (0x13, 12), (0x10, 12), (0x1a, 13), (0x19, 13), (0x18, 13), (0x17, 13), (0x1f, 14), (0x1e, 14), (0x1d, 14),
        (0x1c, 14), (0x1b, 14), (0x1a, 14), (0x19, 14), (0x18, 14), (0x17, 14), (0x16, 14), (0x15, 14), (0x14, 14),
        (0x13, 14), (0x12, 14), (0x11, 14), (0x10, 14), (0x18, 15), (0x17, 15), (0x16, 15), (0x15, 15), (0x14, 15),
        (0x13, 15), (0x12, 15), (0x11, 15), (0x10, 15), (0x03, 3), (0x06, 6), (0x25, 8), (0x0c, 10), (0x1b, 12),
        (0x16, 13), (0x15, 13), (0x1f, 15), (0x1e, 15), (0x1d, 15), (0x1c, 15), (0x1b, 15), (0x1a, 15), (0x19, 15),
        (0x13, 16), (0x12, 16), (0x11, 16), (0x10, 16), (0x05, 4), (0x04, 7), (0x0b, 10), (0x14, 12), (0x14, 13),
        (0x07, 5), (0x24, 8), (0x1c, 12), (0x13, 13), (0x06, 5), (0x0f, 10), (0x12, 12), (0x07, 6), (0x09, 10),
        (0x12, 13), (0x05, 6), (0x1e, 12), (0x14, 16), (0x04, 6), (0x15, 12), (0x07, 7), (0x11, 12), (0x05, 7),
        (0x11, 13), (0x27, 8), (0x10, 13), (0x23, 8), (0x1a, 16), (0x22, 8), (0x19, 16), (0x20, 8), (0x18, 16),
        (0x0e, 10), (0x17, 16), (0x0d, 10), (0x16, 16), (0x08, 10), (0x15, 16), (0x1f, 12), (0x1a, 12), (0x19, 12),
        (0x17, 12), (0x16, 12), (0x1f, 13), (0x1e, 13), (0x1d, 13), (0x1c, 13), (0x1b, 13), (0x1f, 16), (0x1e, 16),
        (0x1d, 16), (0x1c, 16), (0x1b, 16),
    ];

    /// <summary>The end-of-block code of a coefficient table: <c>0110</c> in B.15, <c>10</c> in B.14.</summary>
    public static (uint Code, int Length) EndOfBlock(bool tableOne) => tableOne ? (0b0110u, 4) : (0b10u, 2);

    /// <summary>
    /// The 111 run/level codes of table B.15 (<paramref name="tableOne"/>) or B.14 as used for an intra block's
    /// coefficients after the DC (each code is followed by a sign bit, 1 = negative).
    /// </summary>
    public static IReadOnlyList<(uint Code, int Length, int Run, int Level)> RunLevelCodes(bool tableOne)
    {
        var codes = tableOne ? TableOneCodes : TableZeroCodes;
        var list = new (uint, int, int, int)[codes.Length];
        for (int i = 0; i < codes.Length; i++) list[i] = (codes[i].Code, codes[i].Length, Runs[i], Levels[i]);
        return list;
    }
}
