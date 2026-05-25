// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) ZLS
//
// amSetup.Compression
// Shared first-party codec contract.

namespace AmSetup.Compression;

/// <summary>
/// Defines the byte-oriented compression contract used by amSetup package storage.
/// </summary>
public interface IOwnedCodec
{
    /// <summary>
    /// Gets the codec implemented by this instance.
    /// </summary>
    OwnedCompressionCodec Codec { get; }

    /// <summary>
    /// Compresses the input bytes into the destination stream.
    /// </summary>
    /// <param name="input">The bytes to compress.</param>
    /// <param name="output">The destination stream.</param>
    /// <param name="options">Compression options.</param>
    void Compress(ReadOnlySpan<byte> input, Stream output, CompressionOptions options);

    /// <summary>
    /// Decompresses the input bytes.
    /// </summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="expectedLength">The expected decompressed length when known.</param>
    /// <returns>The decompressed bytes.</returns>
    byte[] Decompress(ReadOnlySpan<byte> input, long? expectedLength = null);
}
