// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) ZLS
//
// amSetup.Compression
// Registry for all owned package codecs.

namespace AmSetup.Compression;

/// <summary>
/// Resolves first-party codecs by codec identifier.
/// </summary>
public static class CodecRegistry
{
    private static readonly IReadOnlyDictionary<OwnedCompressionCodec, IOwnedCodec> Codecs = CreateCodecs();

    /// <summary>
    /// Gets a codec implementation.
    /// </summary>
    /// <param name="codec">The codec identifier.</param>
    /// <returns>The codec implementation.</returns>
    public static IOwnedCodec Get(OwnedCompressionCodec codec)
    {
        if (!Codecs.TryGetValue(codec, out IOwnedCodec? implementation))
            throw new InvalidDataException($"Unknown owned compression codec: {codec}");
        return implementation;
    }

    /// <summary>
    /// Compresses bytes with a registered codec.
    /// </summary>
    /// <param name="input">The bytes to compress.</param>
    /// <param name="options">Compression options.</param>
    /// <returns>The compressed bytes.</returns>
    public static byte[] Compress(ReadOnlySpan<byte> input, CompressionOptions options)
    {
        using var output = new MemoryStream();
        Get(options.Codec).Compress(input, output, options);
        return output.ToArray();
    }

    /// <summary>
    /// Decompresses bytes with a registered codec.
    /// </summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="codec">The codec identifier.</param>
    /// <param name="expectedLength">The expected decompressed length when known.</param>
    /// <returns>The decompressed bytes.</returns>
    public static byte[] Decompress(ReadOnlySpan<byte> input, OwnedCompressionCodec codec, long? expectedLength = null) =>
        Get(codec).Decompress(input, expectedLength);

    /// <summary>
    /// Creates the codec map.
    /// </summary>
    /// <returns>A codec dictionary.</returns>
    private static IReadOnlyDictionary<OwnedCompressionCodec, IOwnedCodec> CreateCodecs()
    {
        IOwnedCodec[] codecs =
        [
            new StoreCodec(),
            new Lz4HcCodec(),
            new FramedLzCodec(OwnedCompressionCodec.BrotliFastest, "AMBRTF1", 4),
            new FramedLzCodec(OwnedCompressionCodec.BrotliBalanced, "AMBRTB1", 4),
            new FramedLzCodec(OwnedCompressionCodec.BrotliSmallest, "AMBRTS1", 4),
            new FramedLzCodec(OwnedCompressionCodec.ZLibFastest, "AMZLBF1", 3),
            new FramedLzCodec(OwnedCompressionCodec.ZLibBalanced, "AMZLBB1", 3),
            new FramedLzCodec(OwnedCompressionCodec.ZLibSmallest, "AMZLBS1", 3),
            new FramedLzCodec(OwnedCompressionCodec.Lzma, "AMLZMA1", 5),
            new FramedLzCodec(OwnedCompressionCodec.Lzma2, "AMLZM21", 5),
            new FramedLzCodec(OwnedCompressionCodec.Aplib, "AMAPLB1", 3),
            new FramedLzCodec(OwnedCompressionCodec.Deflate, "AMDEFL1", 3),
            new FramedLzCodec(OwnedCompressionCodec.GZip, "AMGZIP1", 3),
            new FramedLzCodec(OwnedCompressionCodec.Xz, "AMXXZZ1", 5)
        ];
        return codecs.ToDictionary(codec => codec.Codec);
    }
}
