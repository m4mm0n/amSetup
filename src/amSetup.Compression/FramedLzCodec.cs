// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) ZLS
//
// amSetup.Compression
// First-party framed LZ codec used by the owned Brotli, zlib, LZMA, LZMA2,
// aPLib, DEFLATE, gzip, and XZ package modes.

using System.Buffers.Binary;
using System.Text;

namespace AmSetup.Compression;

/// <summary>
/// Implements a compact first-party LZ77 frame for owned package modes that do not yet share a public container.
/// </summary>
public sealed class FramedLzCodec : IOwnedCodec
{
    private const byte LiteralTag = 0;
    private const byte MatchTag = 1;
    private readonly byte[] magic;
    private readonly int minMatch;

    /// <summary>
    /// Initializes a framed LZ codec.
    /// </summary>
    /// <param name="codec">The codec identity exposed to amSetup.</param>
    /// <param name="magicText">The ASCII frame magic.</param>
    /// <param name="minMatch">The minimum match length.</param>
    public FramedLzCodec(OwnedCompressionCodec codec, string magicText, int minMatch = 4)
    {
        Codec = codec;
        magic = Encoding.ASCII.GetBytes(magicText);
        this.minMatch = minMatch;
    }

    /// <inheritdoc />
    public OwnedCompressionCodec Codec { get; }

    /// <inheritdoc />
    public void Compress(ReadOnlySpan<byte> input, Stream output, CompressionOptions options)
    {
        options = options.Normalize();
        output.Write(magic);
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(header[..8], input.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), Checksums.Crc32(input));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), Checksums.Adler32(input));
        output.Write(header);
        WritePayload(input, output, Math.Clamp(options.Level * 24, 24, 512), Math.Min(options.DictionarySize, 65535));
    }

    /// <inheritdoc />
    public byte[] Decompress(ReadOnlySpan<byte> input, long? expectedLength = null)
    {
        if (input.Length < magic.Length + 16 || !input[..magic.Length].SequenceEqual(magic))
            throw new InvalidDataException($"Invalid {Codec} frame.");

        long length = BinaryPrimitives.ReadInt64LittleEndian(input.Slice(magic.Length, 8));
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(magic.Length + 8, 4));
        uint adler = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(magic.Length + 12, 4));
        if (length < 0 || length > int.MaxValue)
            throw new InvalidDataException($"Invalid {Codec} output length.");
        if (expectedLength is >= 0 && expectedLength != length)
            throw new InvalidDataException($"{Codec} length does not match expected package length.");

        byte[] output = ReadPayload(input[(magic.Length + 16)..], checked((int)length));
        if (Checksums.Crc32(output) != crc || Checksums.Adler32(output) != adler)
            throw new InvalidDataException($"{Codec} checksum mismatch.");
        return output;
    }

    /// <summary>
    /// Writes the framed LZ payload.
    /// </summary>
    /// <param name="input">The uncompressed bytes.</param>
    /// <param name="output">The destination stream.</param>
    /// <param name="searchDepth">The match search depth.</param>
    /// <param name="window">The maximum match distance.</param>
    private void WritePayload(ReadOnlySpan<byte> input, Stream output, int searchDepth, int window)
    {
        int i = 0;
        while (i < input.Length)
        {
            var match = FindMatch(input, i, searchDepth, window);
            if (match.Length >= minMatch)
            {
                output.WriteByte(MatchTag);
                WriteVarInt(output, match.Length);
                WriteVarInt(output, match.Distance);
                i += match.Length;
                continue;
            }

            int start = i++;
            while (i < input.Length)
            {
                match = FindMatch(input, i, searchDepth, window);
                if (match.Length >= minMatch || i - start >= 8192)
                    break;
                i++;
            }

            output.WriteByte(LiteralTag);
            WriteVarInt(output, i - start);
            output.Write(input[start..i]);
        }
    }

    /// <summary>
    /// Reads the framed LZ payload.
    /// </summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="expectedLength">The expected output length.</param>
    /// <returns>The decompressed bytes.</returns>
    private static byte[] ReadPayload(ReadOnlySpan<byte> input, int expectedLength)
    {
        byte[] output = new byte[expectedLength];
        int ip = 0;
        int op = 0;
        while (ip < input.Length)
        {
            byte tag = input[ip++];
            int length = ReadVarInt(input, ref ip);
            if (length < 0 || length > output.Length - op)
                throw new InvalidDataException("Invalid framed LZ length.");
            if (tag == LiteralTag)
            {
                if (length > input.Length - ip)
                    throw new InvalidDataException("Truncated framed LZ literal.");
                input.Slice(ip, length).CopyTo(output.AsSpan(op));
                ip += length;
                op += length;
            }
            else if (tag == MatchTag)
            {
                int distance = ReadVarInt(input, ref ip);
                if (distance <= 0 || distance > op)
                    throw new InvalidDataException("Invalid framed LZ distance.");
                for (int n = 0; n < length; n++)
                    output[op + n] = output[op - distance + n];
                op += length;
            }
            else
            {
                throw new InvalidDataException("Invalid framed LZ tag.");
            }
        }

        if (op != expectedLength)
            throw new InvalidDataException("Framed LZ output length mismatch.");
        return output;
    }

    /// <summary>
    /// Finds the best match at a position.
    /// </summary>
    /// <param name="input">The input bytes.</param>
    /// <param name="position">The current position.</param>
    /// <param name="searchDepth">The maximum number of candidates.</param>
    /// <param name="window">The maximum distance.</param>
    /// <returns>The selected match.</returns>
    private (int Distance, int Length) FindMatch(ReadOnlySpan<byte> input, int position, int searchDepth, int window)
    {
        int bestDistance = 0;
        int bestLength = 0;
        int start = Math.Max(0, position - window);
        int searched = 0;
        for (int candidate = position - 1; candidate >= start && searched < searchDepth; candidate--, searched++)
        {
            if (input[candidate] != input[position])
                continue;
            int length = 0;
            while (position + length < input.Length && input[candidate + length] == input[position + length])
                length++;
            if (length > bestLength)
            {
                bestLength = length;
                bestDistance = position - candidate;
            }
        }

        return (bestDistance, bestLength);
    }

    /// <summary>
    /// Writes a 7-bit variable integer.
    /// </summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="value">The non-negative value.</param>
    private static void WriteVarInt(Stream output, int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            output.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        output.WriteByte((byte)remaining);
    }

    /// <summary>
    /// Reads a 7-bit variable integer.
    /// </summary>
    /// <param name="input">The input bytes.</param>
    /// <param name="offset">The mutable input offset.</param>
    /// <returns>The decoded value.</returns>
    private static int ReadVarInt(ReadOnlySpan<byte> input, ref int offset)
    {
        int value = 0;
        int shift = 0;
        while (shift < 35)
        {
            if (offset >= input.Length)
                throw new InvalidDataException("Truncated variable integer.");
            byte b = input[offset++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }

        throw new InvalidDataException("Variable integer is too large.");
    }
}
