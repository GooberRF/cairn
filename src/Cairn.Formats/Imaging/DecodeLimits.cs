namespace Cairn.Formats.Imaging;

/// <summary>
/// How much work an image decode is allowed to do.
/// <para>
/// The engine accepts textures up to <see cref="EngineFormats.MaxDimension"/> on a side, which at
/// four bytes a pixel is a gigabyte of decoded image — far more than a preview needs, and far more
/// than a file found in a downloaded map pack should be able to make this editor allocate. An
/// eighteen-byte TGA header can claim it. So the decoders check two things before they allocate
/// anything: that the file actually carries the pixel data it says it does, and that the result
/// fits inside the budget below.
/// </para>
/// <para>
/// This limits decoding only. Probing and linting read the header and report the image's true
/// size, so an oversized frame is still measured and compared against frame 0 exactly as before;
/// it is the preview and the thumbnail that say the image is too large to draw.
/// </para>
/// </summary>
public static class DecodeLimits
{
    /// <summary>The most pixels a decoded image may have: 8192 × 8192, or 256 MB in BGRA.</summary>
    public const long MaxDecodePixels = 8192L * 8192L;

    /// <summary>
    /// Throws when an image is too large to decode, with the wording the preview and the thumbnail
    /// show the user.
    /// </summary>
    /// <param name="width">Declared width.</param>
    /// <param name="height">Declared height.</param>
    /// <param name="name">The file name, for the message.</param>
    /// <param name="budget">Pixel budget; defaults to <see cref="MaxDecodePixels"/>.</param>
    public static void EnsureWithinBudget(
        int width, int height, string name, long budget = MaxDecodePixels)
    {
        if ((long)width * height <= budget) return;
        throw new ImageTooLargeException(name, width, height);
    }

    /// <summary>The message a too-large image produces, so tests and the UI can agree on it.</summary>
    public static string TooLargeMessage(string name, int width, int height) =>
        $"'{name}' is {width} x {height}, which is too large to show.";

    /// <summary>
    /// Throws when a file says it holds more pixel data than the bytes that are actually there.
    /// Checked before the buffer is allocated, so a header claiming 16384 × 16384 in an 18-byte
    /// file costs nothing.
    /// </summary>
    /// <param name="needed">Bytes of pixel data the header implies.</param>
    /// <param name="available">Bytes left in the file after the header.</param>
    /// <param name="name">The file name, for the message.</param>
    public static void EnsureDataAvailable(long needed, long available, string name)
    {
        if (needed <= available) return;
        throw new ImageDecodeException(
            $"'{name}' is damaged: its header says the image needs {Size(needed)} of pixel data, "
            + $"but the file only holds {Size(available)}.");
    }

    /// <summary>A byte count the way a person reads one.</summary>
    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        1 => "1 byte",
        _ => $"{bytes} bytes",
    };
}
