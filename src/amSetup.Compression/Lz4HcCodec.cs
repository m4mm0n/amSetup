// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) ZLS
//
// amSetup.Compression
// First-party LZ4 block encoder/decoder with a high-compression match search.

using System.Buffers.Binary;

namespace AmSetup.Compression;

/// <summary>
/// Implements an owned LZ4 block codec with high-compression match selection.
/// </summary>
public sealed class Lz4HcCodec : IOwnedCodec
{
    private const int MinMatch = 4;
    private const int MaxDistance = 65535;
    private static readonly byte[] Magic = "AZL4HC1"u8.ToArray();

    /// <inheritdoc />
    public OwnedCompressionCodec Codec => OwnedCompressionCodec.Lz4Hc;

    /// <inheritdoc />
    public void Compress(ReadOnlySpan<byte> input, Stream output, CompressionOptions options)
    {
        options = options.Normalize();
        output.Write(Magic);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(header, input.Length);
        output.Write(header);
        byte[] compressed = CompressBlock(input, Math.Clamp(options.Level * 32, 32, 512));
        output.Write(compressed);
    }

    /// <inheritdoc />
    public byte[] Decompress(ReadOnlySpan<byte> input, long? expectedLength = null)
    {
        if (input.Length < Magic.Length + 8 || !input[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Invalid LZ4HC frame.");

        long length = BinaryPrimitives.ReadInt64LittleEndian(input.Slice(Magic.Length, 8));
        if (length < 0 || length > int.MaxValue)
            throw new InvalidDataException("Invalid LZ4HC output length.");
        if (expectedLength is >= 0 && expectedLength != length)
            throw new InvalidDataException("LZ4HC length does not match expected package length.");

        return DecompressBlock(input[(Magic.Length + 8)..], checked((int)length));
    }

    /// <summary>
    /// Compresses one LZ4 block.
    /// </summary>
    /// <param name="input">The input bytes.</param>
    /// <param name="searchDepth">The maximum hash-chain search depth.</param>
    /// <returns>The compressed block.</returns>
    private static byte[] CompressBlock(ReadOnlySpan<byte> input, int searchDepth)
    {
        using var output = new MemoryStream(Math.Max(16, input.Length));
        if (input.Length == 0)
            return [];

        int[] head = new int[1 << 16];
        int[] prev = new int[input.Length];
        Array.Fill(head, -1);
        Array.Fill(prev, -1);

        int anchor = 0;
        int i = 0;
        while (i <= input.Length - MinMatch)
        {
            int hash = Hash(input, i);
            int bestLength = 0;
            int bestDistance = 0;
            int candidate = head[hash];
            int searched = 0;
            while (candidate >= 0 && searched++ < searchDepth)
            {
                int distance = i - candidate;
                if (distance > MaxDistance)
                    break;
                int length = CountMatch(input, candidate, i);
                if (length > bestLength)
                {
                    bestLength = length;
                    bestDistance = distance;
                    if (i + length == input.Length)
                        break;
                }

                candidate = prev[candidate];
            }

            prev[i] = head[hash];
            head[hash] = i;

            if (bestLength >= MinMatch)
            {
                WriteSequence(output, input[anchor..i], bestDistance, bestLength);
                int end = i + bestLength;
                for (int p = i + 1; p < end && p <= input.Length - MinMatch; p++)
                {
                    int h = Hash(input, p);
                    prev[p] = head[h];
                    head[h] = p;
                }

                i = end;
                anchor = i;
            }
            else
            {
                i++;
            }
        }

        WriteLastLiterals(output, input[anchor..]);
        return output.ToArray();
    }

    /// <summary>
    /// Decompresses one LZ4 block.
    /// </summary>
    /// <param name="input">The compressed block.</param>
    /// <param name="expectedLength">The expected decompressed length.</param>
    /// <returns>The decompressed bytes.</returns>
    private static byte[] DecompressBlock(ReadOnlySpan<byte> input, int expectedLength)
    {
        byte[] output = new byte[expectedLength];
        int ip = 0;
        int op = 0;
        while (ip < input.Length)
        {
            int token = input[ip++];
            int literalLength = token >> 4;
            if (literalLength == 15)
                literalLength += ReadLength(input, ref ip);
            if (literalLength > input.Length - ip || literalLength > output.Length - op)
                throw new InvalidDataException("Invalid LZ4 literal length.");
            input.Slice(ip, literalLength).CopyTo(output.AsSpan(op));
            ip += literalLength;
            op += literalLength;
            if (ip == input.Length)
                break;
            if (ip + 2 > input.Length)
                throw new InvalidDataException("Truncated LZ4 match offset.");
            int distance = input[ip] | (input[ip + 1] << 8);
            ip += 2;
            if (distance <= 0 || distance > op)
                throw new InvalidDataException("Invalid LZ4 match distance.");
            int matchLength = token & 0x0F;
            if (matchLength == 15)
                matchLength += ReadLength(input, ref ip);
            matchLength += MinMatch;
            if (matchLength > output.Length - op)
                throw new InvalidDataException("Invalid LZ4 match length.");
            for (int n = 0; n < matchLength; n++)
                output[op + n] = output[op - distance + n];
            op += matchLength;
        }

        if (op != expectedLength)
            throw new InvalidDataException("LZ4 output length mismatch.");
        return output;
    }

    /// <summary>
    /// Writes a complete LZ4 sequence.
    /// </summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="literals">The literals before the match.</param>
    /// <param name="distance">The match distance.</param>
    /// <param name="matchLength">The match length.</param>
    private static void WriteSequence(Stream output, ReadOnlySpan<byte> literals, int distance, int matchLength)
    {
        int encodedMatch = matchLength - MinMatch;
        byte token = (byte)(Math.Min(literals.Length, 15) << 4);
        token |= (byte)Math.Min(encodedMatch, 15);
        output.WriteByte(token);
        WriteLength(output, literals.Length - 15);
        output.Write(literals);
        output.WriteByte((byte)distance);
        output.WriteByte((byte)(distance >> 8));
        WriteLength(output, encodedMatch - 15);
    }

    /// <summary>
    /// Writes the terminal literal-only LZ4 sequence.
    /// </summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="literals">The remaining literals.</param>
    private static void WriteLastLiterals(Stream output, ReadOnlySpan<byte> literals)
    {
        output.WriteByte((byte)(Math.Min(literals.Length, 15) << 4));
        WriteLength(output, literals.Length - 15);
        output.Write(literals);
    }

    /// <summary>
    /// Writes an extended LZ4 length.
    /// </summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="length">The extra length.</param>
    private static void WriteLength(Stream output, int length)
    {
        while (length >= 0)
        {
            int value = Math.Min(length, 255);
            output.WriteByte((byte)value);
            length -= 255;
            if (value != 255)
                break;
        }
    }

    /// <summary>
    /// Reads an extended LZ4 length.
    /// </summary>
    /// <param name="input">The compressed block.</param>
    /// <param name="offset">The mutable input offset.</param>
    /// <returns>The decoded extra length.</returns>
    private static int ReadLength(ReadOnlySpan<byte> input, ref int offset)
    {
        int length = 0;
        byte value;
        do
        {
            if (offset >= input.Length)
                throw new InvalidDataException("Truncated LZ4 length.");
            value = input[offset++];
            length += value;
        }
        while (value == 255);
        return length;
    }

    /// <summary>
    /// Counts matching bytes for two positions.
    /// </summary>
    /// <param name="input">The input bytes.</param>
    /// <param name="left">The earlier position.</param>
    /// <param name="right">The later position.</param>
    /// <returns>The match length.</returns>
    private static int CountMatch(ReadOnlySpan<byte> input, int left, int right)
    {
        int length = 0;
        while (right + length < input.Length && input[left + length] == input[right + length])
            length++;
        return length;
    }

    /// <summary>
    /// Hashes four bytes for the LZ4 match table.
    /// </summary>
    /// <param name="input">The input bytes.</param>
    /// <param name="offset">The byte offset.</param>
    /// <returns>A 16-bit hash value.</returns>
    private static int Hash(ReadOnlySpan<byte> input, int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(input[offset..]);
        return (int)((value * 2654435761U) >> 16);
    }
}
