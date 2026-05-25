// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) ZLS
//
// amSetup.Compression
// Small checksum helpers written in managed C# for codec framing.

namespace AmSetup.Compression;

/// <summary>
/// Computes checksums required by first-party compression frames.
/// </summary>
public static class Checksums
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>
    /// Computes an Adler-32 checksum.
    /// </summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <returns>The Adler-32 value.</returns>
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint mod = 65521;
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % mod;
            b = (b + a) % mod;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// Computes a CRC-32 checksum with the standard Ethernet polynomial.
    /// </summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <returns>The CRC-32 value.</returns>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Builds the CRC-32 lookup table.
    /// </summary>
    /// <returns>A 256-entry CRC-32 table.</returns>
    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320U ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }

        return table;
    }
}
