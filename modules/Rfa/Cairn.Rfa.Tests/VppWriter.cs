using System.Buffers.Binary;
using Cairn.Formats.Vpp;

namespace Cairn.Rfa.Tests;

/// <summary>Builds an RF1 version 1 VPP archive in memory, for the archive and resolver tests.</summary>
internal static class VppWriter
{
    public static byte[] Build(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        using var ms = new MemoryStream();

        var header = new byte[VppArchive.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, VppArchive.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)files.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0); // patched below
        ms.Write(header);

        int directoryBytes = Align(files.Count * VppArchive.EntryBytes);
        var directory = new byte[directoryBytes];
        for (int i = 0; i < files.Count; i++)
        {
            var name = System.Text.Encoding.ASCII.GetBytes(files[i].Name);
            if (name.Length >= VppArchive.NameBytes) throw new ArgumentException("name too long");
            name.CopyTo(directory, i * VppArchive.EntryBytes);
            BinaryPrimitives.WriteInt32LittleEndian(
                directory.AsSpan(i * VppArchive.EntryBytes + VppArchive.NameBytes), files[i].Data.Length);
        }
        ms.Write(directory);

        foreach (var (_, data) in files)
        {
            ms.Write(data);
            int padding = Align(data.Length) - data.Length;
            ms.Write(new byte[padding]);
        }

        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)bytes.Length);
        return bytes;
    }

    private static int Align(int value) =>
        (value + VppArchive.BlockSize - 1) / VppArchive.BlockSize * VppArchive.BlockSize;
}
