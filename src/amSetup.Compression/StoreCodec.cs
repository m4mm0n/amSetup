// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) ZLS
//
// amSetup.Compression
// Uncompressed first-party codec.

namespace AmSetup.Compression;

/// <summary>
/// Implements the uncompressed store codec.
/// </summary>
public sealed class StoreCodec : IOwnedCodec
{
    /// <inheritdoc />
    public OwnedCompressionCodec Codec => OwnedCompressionCodec.Store;

    /// <inheritdoc />
    public void Compress(ReadOnlySpan<byte> input, Stream output, CompressionOptions options) =>
        output.Write(input);

    /// <inheritdoc />
    public byte[] Decompress(ReadOnlySpan<byte> input, long? expectedLength = null)
    {
        if (expectedLength is >= 0 && input.Length != expectedLength)
            throw new InvalidDataException("Stored payload length does not match the package header.");
        return input.ToArray();
    }
}
