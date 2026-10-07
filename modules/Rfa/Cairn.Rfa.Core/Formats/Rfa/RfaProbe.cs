using System.Numerics;

namespace Cairn.Rfa.Formats.Rfa;

/// <summary>The header facts of a clip, read without touching its keys.</summary>
/// <param name="Version">7 or 8.</param>
/// <param name="PosReduction">Exporter position tolerance.</param>
/// <param name="RotReduction">Exporter rotation tolerance.</param>
/// <param name="StartTime">Ticks.</param>
/// <param name="EndTime">Ticks.</param>
/// <param name="BoneCount">Number of bone tracks (must equal the mesh's bone count to play correctly).</param>
/// <param name="MorphVertexCount">Number of morphed vertices.</param>
/// <param name="MorphKeyframeCount">Number of morph keyframes.</param>
/// <param name="RampIn">Ticks.</param>
/// <param name="RampOut">Ticks.</param>
/// <param name="TotalRotation">Raw header field, unused by the game.</param>
/// <param name="TotalTranslation">Raw header field, unused by the game.</param>
public sealed record RfaProbeResult(
    int Version,
    float PosReduction,
    float RotReduction,
    int StartTime,
    int EndTime,
    int BoneCount,
    int MorphVertexCount,
    int MorphKeyframeCount,
    int RampIn,
    int RampOut,
    Quaternion TotalRotation,
    Vector3 TotalTranslation)
{
    /// <summary>End minus start, in ticks.</summary>
    public int Duration => EndTime - StartTime;

    /// <summary>True when the clip carries morph (vertex) animation.</summary>
    public bool HasMorph => MorphVertexCount > 0;
}

/// <summary>
/// Reads an .rfa header (the first 0x50 bytes) for the library: version, timing, bone count and
/// morph counts. Validates the signature and version and that the counts are not negative.
/// </summary>
public static class RfaProbe
{
    /// <summary>Probes a file on disk, reading only its header.</summary>
    /// <exception cref="AssetFormatException">The file is not a version 7 or 8 clip.</exception>
    public static RfaProbeResult ProbeFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Probe(stream, Path.GetFileName(path));
    }

    /// <summary>Probes a stream from its current position.</summary>
    public static RfaProbeResult Probe(Stream stream, string name) =>
        Probe(FileBytes.ReadPrefix(stream, RfaClip.BoneTableOffset), name);

    /// <summary>Probes an in-memory file (or at least its first 0x50 bytes).</summary>
    public static RfaProbeResult Probe(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ReadHeader(new BinaryCursor(data, name));
    }

    internal static RfaProbeResult ReadHeader(BinaryCursor r)
    {
        if (r.Length < RfaClip.BoneTableOffset)
            throw new AssetFormatException($"'{r.Name}' is too short to be an RFA clip ({r.Length} bytes).");
        r.Position = 0;
        uint signature = r.ReadUInt32();
        if (signature != RfaClip.Signature)
            throw new AssetFormatException($"'{r.Name}' is not an RFA clip (signature 0x{signature:X8}, expected \"VMVF\").");
        int version = r.ReadInt32();
        if (version is not (7 or 8))
            throw new AssetFormatException($"'{r.Name}' is RFA version {version}; only versions 7 and 8 are supported.");
        float posReduction = r.ReadSingle();
        float rotReduction = r.ReadSingle();
        int start = r.ReadInt32();
        int end = r.ReadInt32();
        int bones = r.ReadInt32();
        int morphVertices = r.ReadInt32();
        int morphKeyframes = r.ReadInt32();
        int rampIn = r.ReadInt32();
        int rampOut = r.ReadInt32();
        var totalRotation = r.ReadQuaternion();
        var totalTranslation = r.ReadVector3();
        if (bones < 0) throw r.Fail($"the header declares {bones} bones.");
        if (morphVertices < 0) throw r.Fail($"the header declares {morphVertices} morph vertices.");
        if (morphKeyframes < 0) throw r.Fail($"the header declares {morphKeyframes} morph keyframes.");
        return new RfaProbeResult(version, posReduction, rotReduction, start, end, bones, morphVertices,
            morphKeyframes, rampIn, rampOut, totalRotation, totalTranslation);
    }
}
