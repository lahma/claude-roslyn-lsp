using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

/// <summary>
/// Reads the glibc versions an ELF executable needs at load time, from its GNU version-needs section
/// (<c>.gnu.version_r</c>, the table <c>readelf -V</c> prints).
/// </summary>
/// <remarks>
/// A Linux Native AOT binary is linked against the glibc of the machine that built it and refuses to
/// start on an older one (<c>version 'GLIBC_2.38' not found</c>). The highest <c>GLIBC_x.y</c> it
/// needs is therefore the oldest Linux it runs on, and that floor moves without anyone touching the
/// repository whenever the runner image does. Reading it back after every publish is what turns such a
/// move into a failed build (R3). Only 64-bit little-endian ELF is handled, which is what linux-x64 and
/// linux-arm64 produce; anything else is rejected rather than guessed at.
/// </remarks>
internal static class GlibcRequirements
{
    /// <summary><c>SHT_GNU_verneed</c>.</summary>
    private const uint VersionNeedsSectionType = 0x6ffffffe;

    private const string Prefix = "GLIBC_";

    /// <summary>
    /// Every <c>GLIBC_x.y[.z]</c> version the executable at <paramref name="path"/> names in its
    /// version-needs section, ascending and without duplicates. Empty for a binary that needs no glibc
    /// symbol version (a musl build, for one).
    /// </summary>
    public static IReadOnlyList<Version> Read(string path)
    {
        var elf = File.ReadAllBytes(path);

        if (elf.Length < 64 || elf[0] != 0x7F || elf[1] != (byte)'E' || elf[2] != (byte)'L' || elf[3] != (byte)'F')
        {
            throw new InvalidDataException($"'{path}' is not an ELF file");
        }

        if (elf[4] != 2 || elf[5] != 1)
        {
            throw new InvalidDataException($"'{path}' is not a 64-bit little-endian ELF file");
        }

        // ELF64 header: e_shoff at 0x28, e_shentsize at 0x3A, e_shnum at 0x3C.
        var sectionTable = checked((int)UInt64(elf, 0x28));
        var sectionHeaderSize = UInt16(elf, 0x3A);
        var sectionCount = UInt16(elf, 0x3C);

        var versions = new SortedSet<Version>();
        var found = false;

        for (var index = 0; index < sectionCount; index++)
        {
            // Section header: sh_type at +4, sh_offset at +24, sh_link at +40, sh_info at +44.
            var header = sectionTable + index * sectionHeaderSize;
            if (UInt32(elf, header + 4) != VersionNeedsSectionType)
            {
                continue;
            }

            found = true;
            var entryCount = UInt32(elf, header + 44);
            var stringsHeader = sectionTable + checked((int)UInt32(elf, header + 40)) * sectionHeaderSize;
            var strings = checked((int)UInt64(elf, stringsHeader + 24));

            // Verneed: vn_cnt at +2, vn_aux at +8, vn_next at +12. Vernaux: vna_name at +8,
            // vna_next at +12. Both "next" fields are offsets relative to the current entry.
            var need = checked((int)UInt64(elf, header + 24));
            for (var entry = 0; entry < entryCount; entry++)
            {
                var auxiliaryCount = UInt16(elf, need + 2);
                var auxiliary = need + checked((int)UInt32(elf, need + 8));

                for (var item = 0; item < auxiliaryCount; item++)
                {
                    var name = NullTerminated(elf, strings + checked((int)UInt32(elf, auxiliary + 8)));
                    if (name.StartsWith(Prefix, StringComparison.Ordinal) &&
                        Version.TryParse(name[Prefix.Length..], out var version))
                    {
                        versions.Add(version);
                    }

                    auxiliary += checked((int)UInt32(elf, auxiliary + 12));
                }

                need += checked((int)UInt32(elf, need + 12));
            }
        }

        if (!found)
        {
            // A dynamically linked executable always has one. Without it the requirement is unknown,
            // and reporting "nothing needed" would pass the very check this exists for.
            throw new InvalidDataException(
                $"'{path}' has no GNU version-needs section, so the glibc it needs cannot be read");
        }

        return versions.ToArray();
    }

    private static ushort UInt16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));

    private static uint UInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    private static ulong UInt64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));

    private static string NullTerminated(byte[] data, int offset)
    {
        var length = Array.IndexOf(data, (byte)0, offset) - offset;
        return Encoding.ASCII.GetString(data, offset, length);
    }
}