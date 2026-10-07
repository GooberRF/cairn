namespace Cairn.Atx.Linting;

/// <summary>Every lint rule id, so call sites never spell a code by hand.</summary>
public static class AtxRules
{
    public const string Syntax = "ATX001";
    public const string NoFrames = "ATX002";
    public const string FrameFileMissing = "ATX003";
    public const string NestedAtx = "ATX004";
    public const string UnknownFormat = "ATX005";
    public const string UnknownMaterial = "ATX006";
    public const string BadStructure = "ATX007";
    public const string FrameImageNotFound = "ATX010";
    public const string FrameMismatch = "ATX011";
    public const string MaskNotFound = "ATX012";
    public const string MaskMismatch = "ATX013";
    public const string MaskNotGreyscale = "ATX014";
    public const string CompressedTransform = "ATX015";
    public const string ImageUnreadable = "ATX016";
    public const string WrongType = "ATX020";
    public const string FrameTimeTooSmall = "ATX021";
    public const string AnimationModeOutOfRange = "ATX022";
    public const string UnknownKey = "ATX023";
    public const string UnknownTopLevel = "ATX024";
    public const string PathSeparator = "ATX026";
    public const string NameTooLong = "ATX027";
    public const string UnsupportedExtension = "ATX028";
    public const string InitiallyOnWithStatic = "ATX030";
    public const string SingleFrameAnimated = "ATX031";
    public const string RedundantFrameTime = "ATX033";
    public const string RedundantMaterial = "ATX034";
    public const string MaskPromotesFormat = "ATX035";
    public const string MaskWith1555 = "ATX036";
    public const string EmptyString = "ATX037";

    /// <summary>Rules that need image files on disk, and therefore run asynchronously.</summary>
    public static IReadOnlyList<string> AssetRules { get; } =
    [
        FrameImageNotFound, FrameMismatch, MaskNotFound, MaskMismatch,
        MaskNotGreyscale, CompressedTransform, ImageUnreadable,
    ];
}
