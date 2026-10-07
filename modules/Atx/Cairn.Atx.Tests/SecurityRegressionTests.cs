using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Atx.SampleGen;

namespace Cairn.Atx.Tests;

/// <summary>
/// The threat model is a map pack downloaded from the internet and opened here: every filename,
/// header field and byte in it was written by somebody else. One test per security-review finding.
/// </summary>
public class SecurityRegressionTests
{
    private static byte[] Tga(int size, byte shade) =>
        TinyImageWriter.Tga24(size, size, [.. Enumerable.Repeat(shade, size * size * 3)]);

    // ── Sound by construction: a frame name can never leave the search locations ─

    [Theory]
    [InlineData(@"\\host\share\a.tga")]
    [InlineData(@"..\..\..\Windows\System32\config\SAM")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("CON")]
    [InlineData("*.tga")]
    [InlineData("?.tga")]
    [InlineData("a.tga:stream")]
    public void HostileFrameNamesNeverEscapeTheSearchFolders(string requested)
    {
        using var temp = new TempFolder();
        string folder = temp.Path;
        File.WriteAllBytes(Path.Combine(folder, "real.tga"), Tga(4, 9));

        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = folder });
        var found = resolver.Resolve(requested);

        // Either nothing is found, or what is found is a file in the folder we pointed it at —
        // never a path the name itself steered it to.
        if (found?.FilePath is { } path)
        {
            Assert.Equal(
                Path.GetFullPath(folder),
                Path.GetFullPath(Path.GetDirectoryName(path)!),
                ignoreCase: true);
        }
        Assert.Null(found?.ArchivePath);
    }

    // ── S1: a header cannot make the decoder allocate what the file does not hold ─

    [Fact]
    public void S1_ATinyTgaClaimingAHugeImageIsRefusedNotAllocated()
    {
        // An 18-byte TGA header declaring 16384 x 16384: 1 GB of pixels if it were believed.
        byte[] bomb = new byte[18];
        bomb[2] = 2;                       // uncompressed true-colour
        bomb[12] = 0x00; bomb[13] = 0x40;  // width  16384
        bomb[14] = 0x00; bomb[15] = 0x40;  // height 16384
        bomb[16] = 24;                     // 24-bit

        var error = Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(bomb, "bomb.tga"));
        Assert.IsNotType<ImageTooLargeException>(error);   // refused for being truncated, not for size
        Assert.Contains("is damaged", error.Message, StringComparison.Ordinal);
        Assert.Contains("768 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S1_ProbingAHugeImageStillReportsItsRealSize()
    {
        byte[] bomb = new byte[18];
        bomb[2] = 2;
        bomb[12] = 0x00; bomb[13] = 0x40;
        bomb[14] = 0x00; bomb[15] = 0x40;
        bomb[16] = 24;

        // Linting must still measure the frame and compare it against frame 0. Only pixels are off.
        var info = ImageProbe.Probe(bomb, "bomb.tga");
        Assert.Equal(16384, info.Width);
        Assert.Equal(16384, info.Height);
    }

    [Fact]
    public void S1_AnImageInsideTheEnginesLimitButOverTheBudgetIsRefusedPolitely()
    {
        // 16384 x 16384 with all the pixels really there would be within the engine's limit but
        // well past the decode budget, so it must fail as "too large", not as corrupt.
        Assert.True(DecodeLimits.MaxDecodePixels < (long)EngineFormats.MaxDimension * EngineFormats.MaxDimension);
        var error = Assert.Throws<ImageTooLargeException>(
            () => DecodeLimits.EnsureWithinBudget(16384, 16384, "huge.tga"));
        Assert.Contains("too large to show", error.Message, StringComparison.Ordinal);
        Assert.Equal(16384, error.Width);
    }

    [Fact]
    public void S1_NormalImagesStillDecode()
    {
        var image = ImageDecoder.Decode(Tga(8, 42), "small.tga");
        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);
    }

    // ── S6: a VPP cannot make the reader allocate its declared directory ─────

    [Fact]
    public void S6_AStubArchiveClaimingAMillionFilesIsRefused()
    {
        using var temp = new TempFolder();
        byte[] stub = new byte[2048];
        BitConverter.GetBytes(VppArchive.Signature).CopyTo(stub, 0);   // signature
        BitConverter.GetBytes(1u).CopyTo(stub, 4);            // version
        BitConverter.GetBytes(1_000_000u).CopyTo(stub, 8);    // file count
        string path = temp.Write("stub.vpp", stub);

        var error = Assert.Throws<VppFormatException>(() => VppArchive.Open(path));
        Assert.Contains("not big enough", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S6_RealArchivesStillOpen()
    {
        using var temp = new TempFolder();
        string path = temp.Write("pack.vpp", VppWriter.Build([("a.tga", Tga(4, 1))]));
        Assert.Single(VppArchive.Open(path).Entries);
    }

    // ── S16: VBM mip arithmetic does not overflow ────────────────────────────

    [Fact]
    public void S16_AVbmHeaderWithHugeDimensionsAndManyFramesDoesNotOverflow()
    {
        byte[] header = new byte[32];
        BitConverter.GetBytes(VbmCodec.Signature).CopyTo(header, 0);
        BitConverter.GetBytes(1u).CopyTo(header, 4);
        BitConverter.GetBytes(16384u).CopyTo(header, 8);    // width
        BitConverter.GetBytes(16384u).CopyTo(header, 12);   // height
        BitConverter.GetBytes(0u).CopyTo(header, 16);       // format 1555
        BitConverter.GetBytes(0u).CopyTo(header, 20);       // fps
        BitConverter.GetBytes(10000u).CopyTo(header, 24);   // frame count
        BitConverter.GetBytes(16u).CopyTo(header, 28);      // mip field

        // The point is that it answers at all, with a plausible count, rather than wrapping into a
        // size that happens to "match" the 32-byte file. The field claims 16 levels below the base,
        // which is one more than a 16384-pixel chain can hold, so the answer is capped at 15.
        var info = VbmCodec.Probe(header, "huge.vbm");
        Assert.Equal(16384, info.Width);
        Assert.Equal(15, info.MipLevels);

        // And decoding it is refused before anything is allocated: the pixels it claims are simply
        // not in the file.
        var error = Assert.Throws<ImageDecodeException>(() => VbmCodec.Decode(header, "huge.vbm"));
        Assert.Contains("is damaged", error.Message, StringComparison.Ordinal);
    }
}
