namespace Cairn.Lvl.Model;

/// <summary>
/// Face classes for filtering and preview. A face can be in several classes (a scrolling sky face); a face in none
/// of them is <see cref="Normal"/>.
/// </summary>
[Flags]
public enum RfFaceClass
{
    Normal = 0,
    Portal = 0x001,
    Detail = 0x002,
    Sky = 0x004,
    Invisible = 0x008,
    Hole = 0x010,
    LiquidSurface = 0x020,
    Alpha = 0x040,
    FullBright = 0x080,
    Scrolling = 0x100,
}

/// <summary>Where a face comes from, which decides how two of its classes are found.</summary>
public enum RfGeometrySource
{
    /// <summary>The compiled level geometry (and movers): every class comes from the face itself.</summary>
    Compiled,
    /// <summary>An editor brush: portal and detail come from the brush's solid flags.</summary>
    Brush,
}

/// <summary>Computes face classes, each from the flag that really carries it.</summary>
public static class RfFaceClasses
{
    /// <summary>Every class, in display order.</summary>
    public static IReadOnlyList<RfFaceClass> All { get; } =
    [
        RfFaceClass.Portal, RfFaceClass.Detail, RfFaceClass.Sky, RfFaceClass.Invisible, RfFaceClass.Hole,
        RfFaceClass.LiquidSurface, RfFaceClass.Alpha, RfFaceClass.FullBright, RfFaceClass.Scrolling,
    ];

    /// <summary>A face of the compiled geometry or of a mover.</summary>
    public static RfFaceClass OfCompiled(RfFace face)
    {
        var c = FromFaceFlags(face.Flags);
        // Portal faces are not a flag: the level build marks them with their portal index + 2.
        if (face.PortalPlus2 >= 2) c |= RfFaceClass.Portal;
        if ((face.Flags & RfFaceFlags.Detail) != 0) c |= RfFaceClass.Detail;
        if ((face.Flags & RfFaceFlags.LiquidSurface) != 0) c |= RfFaceClass.LiquidSurface;
        return c;
    }

    /// <summary>
    /// A face of an editor brush. Face bit 0x08 is an editor bit there, so "detail" comes from the brush's solid flag,
    /// and "portal" from its portal flag.
    /// </summary>
    public static RfFaceClass OfBrushFace(RfFace face, RfBrush brush)
    {
        var c = FromFaceFlags(face.Flags);
        if (brush.IsPortal) c |= RfFaceClass.Portal;
        if (brush.IsDetail) c |= RfFaceClass.Detail;
        return c;
    }

    public static RfFaceClass Of(RfFace face, RfGeometrySource source, RfBrush? brush) =>
        source == RfGeometrySource.Brush && brush is not null ? OfBrushFace(face, brush) : OfCompiled(face);

    private static RfFaceClass FromFaceFlags(RfFaceFlags f)
    {
        var c = RfFaceClass.Normal;
        if ((f & RfFaceFlags.ShowSky) != 0) c |= RfFaceClass.Sky;
        if ((f & RfFaceFlags.Invisible) != 0) c |= RfFaceClass.Invisible;
        if ((f & RfFaceFlags.HasHoles) != 0) c |= RfFaceClass.Hole;
        if ((f & RfFaceFlags.HasAlpha) != 0) c |= RfFaceClass.Alpha;
        if ((f & RfFaceFlags.FullBright) != 0) c |= RfFaceClass.FullBright;
        if ((f & RfFaceFlags.ScrollTexture) != 0) c |= RfFaceClass.Scrolling;
        return c;
    }

    /// <summary>A short user-facing name ("Liquid surface").</summary>
    public static string DisplayName(RfFaceClass c) => c switch
    {
        RfFaceClass.Normal => "Normal",
        RfFaceClass.Portal => "Portal",
        RfFaceClass.Detail => "Detail",
        RfFaceClass.Sky => "Sky",
        RfFaceClass.Invisible => "Invisible",
        RfFaceClass.Hole => "Holes",
        RfFaceClass.LiquidSurface => "Liquid surface",
        RfFaceClass.Alpha => "Alpha",
        RfFaceClass.FullBright => "Full bright",
        RfFaceClass.Scrolling => "Scrolling",
        _ => c.ToString(),
    };
}
