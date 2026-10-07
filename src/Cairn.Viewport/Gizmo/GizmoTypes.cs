namespace Cairn.Viewport;

/// <summary>The viewport's gizmo tool (Q / E / W).</summary>
public enum PoseTool
{
    /// <summary>Click to select; no gizmo.</summary>
    Select,
    /// <summary>Rotate gizmo on the active item.</summary>
    Rotate,
    /// <summary>Move gizmo on the active item.</summary>
    Move,
    /// <summary>Scale gizmo on the active item (only targets implementing <see cref="IGizmoScaleTarget"/>).</summary>
    Scale,
}

/// <summary>Which gizmo the viewport draws now.</summary>
public enum PoseGizmo
{
    None,
    Rotate,
    Move,
    /// <summary>Per-axis cubes and a uniform centre square; drags go to <see cref="IGizmoScaleTarget.UpdateScale"/>.</summary>
    Scale,
}

/// <summary>What a drag does.</summary>
public enum PoseDragKind
{
    Rotate,
    Move,
    Ik,
    /// <summary>A scale drag (axis cube or the uniform centre, <see cref="GizmoHandle.Screen"/>).</summary>
    Scale,
}

/// <summary>
/// Optional extension of <see cref="IGizmoTarget"/>: a target that reports <see cref="PoseGizmo.Scale"/> receives
/// scale drags here (after <see cref="IGizmoTarget.BeginDrag"/> with <see cref="PoseDragKind.Scale"/>).
/// </summary>
public interface IGizmoScaleTarget
{
    /// <summary>
    /// Scale factor since the drag started: along the space's axis <paramref name="handle"/> (0..2, in the placement
    /// frame), along the two axes of a plane handle (<see cref="GizmoHandle.PlaneYZ"/>, <see cref="GizmoHandle.PlaneZX"/>,
    /// <see cref="GizmoHandle.PlaneXY"/>; <see cref="GizmoHandle.ScaleVector"/> turns any handle into per-axis factors)
    /// or uniformly (<see cref="GizmoHandle.Screen"/>). 1 = unchanged; never below 0.01.
    /// </summary>
    void UpdateScale(int handle, double factor);
}

/// <summary>Gizmo handle ids shared by the targets and the viewport layer.</summary>
public static class GizmoHandle
{
    public const int None = -1;
    public const int X = 0;
    public const int Y = 1;
    public const int Z = 2;
    /// <summary>The screen ring (rotate) or the screen-plane square (move).</summary>
    public const int Screen = 3;
    /// <summary>The free trackball inside the rings.</summary>
    public const int Free = 4;
    /// <summary>A sphere's outline: dragging it changes the radius.</summary>
    public const int Radius = 5;
    /// <summary>The square between the Y and Z arrows (move / scale in Y and Z; the plane's normal is X).</summary>
    public const int PlaneYZ = 6;
    /// <summary>The square between the Z and X arrows (move / scale in Z and X; the plane's normal is Y).</summary>
    public const int PlaneZX = 7;
    /// <summary>The square between the X and Y arrows (move / scale in X and Y; the plane's normal is Z).</summary>
    public const int PlaneXY = 8;

    /// <summary>True for the three two-axis plane handles.</summary>
    public static bool IsPlane(int handle) => handle is PlaneYZ or PlaneZX or PlaneXY;

    /// <summary>The axis (0..2) a plane handle's plane is perpendicular to.</summary>
    public static int PlaneNormal(int handle) => handle - PlaneYZ;

    /// <summary>The plane handle perpendicular to <paramref name="axis"/> (0..2).</summary>
    public static int PlaneOf(int axis) => PlaneYZ + axis;

    /// <summary>Per-axis scale factors (placement frame) for a scale drag of <paramref name="handle"/> by <paramref name="factor"/>.</summary>
    public static System.Numerics.Vector3 ScaleVector(int handle, float factor) => handle switch
    {
        X => new(factor, 1, 1),
        Y => new(1, factor, 1),
        Z => new(1, 1, factor),
        PlaneYZ => new(1, factor, factor),
        PlaneZX => new(factor, 1, factor),
        PlaneXY => new(factor, factor, 1),
        _ => new(factor),
    };

    /// <summary>"X", "Y", "Z", "Screen", "Free", "Radius", "YZ", "ZX", "XY".</summary>
    public static string Name(int handle) => handle switch
    {
        X => "X",
        Y => "Y",
        Z => "Z",
        Screen => "Screen",
        Free => "Free",
        Radius => "Radius",
        PlaneYZ => "YZ",
        PlaneZX => "ZX",
        PlaneXY => "XY",
        _ => string.Empty,
    };
}
