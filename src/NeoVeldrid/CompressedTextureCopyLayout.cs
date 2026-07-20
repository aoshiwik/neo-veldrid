namespace NeoVeldrid;

/// <summary>
/// Describes the source mip and dense destination storage for a compressed
/// texture-region copy.
/// </summary>
internal readonly struct CompressedTextureCopyLayout
{
    public uint FullRowPitch { get; }
    public uint FullDepthPitch { get; }
    public uint FullArrayPitch { get; }
    public uint DenseRowPitch { get; }
    public uint DenseDepthPitch { get; }
    public uint DenseCopySizeInBytes { get; }
    public uint SourceArrayLayerOffset { get; }

    private CompressedTextureCopyLayout(
        uint fullRowPitch,
        uint fullDepthPitch,
        uint fullArrayPitch,
        uint denseRowPitch,
        uint denseDepthPitch,
        uint denseCopySizeInBytes,
        uint sourceArrayLayerOffset)
    {
        FullRowPitch = fullRowPitch;
        FullDepthPitch = fullDepthPitch;
        FullArrayPitch = fullArrayPitch;
        DenseRowPitch = denseRowPitch;
        DenseDepthPitch = denseDepthPitch;
        DenseCopySizeInBytes = denseCopySizeInBytes;
        SourceArrayLayerOffset = sourceArrayLayerOffset;
    }

    public static CompressedTextureCopyLayout Create(
        uint mipWidth,
        uint mipHeight,
        uint mipDepth,
        uint copyWidth,
        uint copyHeight,
        uint copyDepth,
        uint sourceArrayLayer,
        PixelFormat format)
    {
        if (!FormatHelpers.IsCompressedFormat(format))
        {
            throw new NeoVeldridException(
                $"{format} is not a compressed texture format.");
        }

        uint fullRowPitch = FormatHelpers.GetRowPitch(mipWidth, format);
        uint fullDepthPitch = FormatHelpers.GetDepthPitch(
            fullRowPitch,
            mipHeight,
            format);
        uint fullArrayPitch = checked(fullDepthPitch * mipDepth);

        uint denseRowPitch = FormatHelpers.GetRowPitch(copyWidth, format);
        uint denseDepthPitch = FormatHelpers.GetDepthPitch(
            denseRowPitch,
            copyHeight,
            format);

        return new CompressedTextureCopyLayout(
            fullRowPitch,
            fullDepthPitch,
            fullArrayPitch,
            denseRowPitch,
            denseDepthPitch,
            checked(denseDepthPitch * copyDepth),
            checked(fullArrayPitch * sourceArrayLayer));
    }
}
