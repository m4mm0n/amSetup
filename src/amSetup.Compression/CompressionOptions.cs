// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) ZLS
//
// amSetup.Compression
// First-party managed compression options. This file intentionally has no
// dependency on native codecs or external compression packages.

namespace AmSetup.Compression;

/// <summary>
/// Identifies the first-party compression codec used by an amSetup package payload.
/// </summary>
public enum OwnedCompressionCodec
{
    /// <summary>Stores the payload without compression.</summary>
    Store,

    /// <summary>Uses the owned Brotli-mode encoder tuned for fastest output.</summary>
    BrotliFastest,

    /// <summary>Uses the owned Brotli-mode encoder with balanced settings.</summary>
    BrotliBalanced,

    /// <summary>Uses the owned Brotli-mode encoder tuned for smallest output.</summary>
    BrotliSmallest,

    /// <summary>Uses the owned zlib-mode encoder tuned for fastest output.</summary>
    ZLibFastest,

    /// <summary>Uses the owned zlib-mode encoder with balanced settings.</summary>
    ZLibBalanced,

    /// <summary>Uses the owned zlib-mode encoder tuned for smallest output.</summary>
    ZLibSmallest,

    /// <summary>Uses the owned LZMA-mode encoder.</summary>
    Lzma,

    /// <summary>Uses the owned LZMA2-mode encoder.</summary>
    Lzma2,

    /// <summary>Uses the owned high-compression LZ4 block encoder.</summary>
    Lz4Hc,

    /// <summary>Uses the owned aPLib-mode encoder.</summary>
    Aplib,

    /// <summary>Uses the owned raw DEFLATE-mode encoder.</summary>
    Deflate,

    /// <summary>Uses the owned gzip-mode encoder.</summary>
    GZip,

    /// <summary>Uses the owned XZ-mode encoder.</summary>
    Xz
}

/// <summary>
/// Provides compression settings shared by the owned codecs.
/// </summary>
/// <param name="Codec">The codec to use.</param>
/// <param name="Level">The compression level from 1 through 9.</param>
/// <param name="DictionarySize">The maximum history window size.</param>
public sealed record CompressionOptions(OwnedCompressionCodec Codec, int Level = 5, int DictionarySize = 1 << 20)
{
    /// <summary>
    /// Returns the options with values clamped to supported ranges.
    /// </summary>
    /// <returns>A normalized options instance.</returns>
    public CompressionOptions Normalize() => this with
    {
        Level = Math.Clamp(Level, 1, 9),
        DictionarySize = Math.Clamp(DictionarySize, 4 * 1024, 16 * 1024 * 1024)
    };
}
