using System;

namespace NeoVeldrid;

/// <summary>
/// Defines the backend-independent, tightly packed byte layout used by
/// buffer-backed staging textures.
/// </summary>
internal readonly struct TextureStagingLayout
{
    private readonly uint _width;
    private readonly uint _height;
    private readonly uint _depth;
    private readonly uint _mipLevels;
    private readonly uint _arrayLayers;
    private readonly PixelFormat _format;
    private readonly uint _layerPitch;

    internal uint TotalSizeInBytes { get; }

    private TextureStagingLayout(
        uint width,
        uint height,
        uint depth,
        uint mipLevels,
        uint arrayLayers,
        PixelFormat format,
        uint layerPitch,
        uint totalSizeInBytes)
    {
        _width = width;
        _height = height;
        _depth = depth;
        _mipLevels = mipLevels;
        _arrayLayers = arrayLayers;
        _format = format;
        _layerPitch = layerPitch;
        TotalSizeInBytes = totalSizeInBytes;
    }

    internal static TextureStagingLayout Create(
        in TextureDescription description) =>
        Create(
            description.Width,
            description.Height,
            description.Depth,
            description.MipLevels,
            description.ArrayLayers,
            description.Format);

    internal static TextureStagingLayout Create(
        uint width,
        uint height,
        uint depth,
        uint mipLevels,
        uint arrayLayers,
        PixelFormat format)
    {
        if (width == 0 || height == 0 || depth == 0 ||
            mipLevels == 0 || arrayLayers == 0)
        {
            throw new NeoVeldridException(
                "Staging texture dimensions, mip levels, and array layers must be non-zero.");
        }

        try
        {
            ulong layerPitch = 0;
            for (uint mipLevel = 0; mipLevel < mipLevels; mipLevel++)
            {
                GetMipDimensions(
                    width,
                    height,
                    depth,
                    mipLevel,
                    out uint mipWidth,
                    out uint mipHeight,
                    out uint mipDepth);
                layerPitch = checked(
                    layerPitch +
                    FormatHelpers.GetRegionSize(
                        mipWidth,
                        mipHeight,
                        mipDepth,
                        format));
            }

            ulong totalSize = checked(layerPitch * arrayLayers);
            if (layerPitch > uint.MaxValue || totalSize > uint.MaxValue)
            {
                throw LayoutTooLarge();
            }

            return new TextureStagingLayout(
                width,
                height,
                depth,
                mipLevels,
                arrayLayers,
                format,
                (uint)layerPitch,
                (uint)totalSize);
        }
        catch (OverflowException exception)
        {
            throw LayoutTooLarge(exception);
        }
    }

    internal StagingTextureSubresourceLayout GetSubresourceLayout(
        uint mipLevel,
        uint arrayLayer)
    {
        if (mipLevel >= _mipLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(mipLevel));
        }
        if (arrayLayer >= _arrayLayers)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayLayer));
        }

        uint mipOffset = 0;
        for (uint level = 0; level < mipLevel; level++)
        {
            GetMipDimensions(
                _width,
                _height,
                _depth,
                level,
                out uint previousWidth,
                out uint previousHeight,
                out uint previousDepth);
            mipOffset = checked(
                mipOffset +
                FormatHelpers.GetRegionSize(
                    previousWidth,
                    previousHeight,
                    previousDepth,
                    _format));
        }

        GetMipDimensions(
            _width,
            _height,
            _depth,
            mipLevel,
            out uint width,
            out uint height,
            out uint depth);
        uint rowPitch = FormatHelpers.GetRowPitch(width, _format);
        uint depthPitch = FormatHelpers.GetDepthPitch(
            rowPitch,
            height,
            _format);
        uint sizeInBytes = checked(depthPitch * depth);
        uint offset = checked((arrayLayer * _layerPitch) + mipOffset);

        return new StagingTextureSubresourceLayout(
            offset,
            sizeInBytes,
            rowPitch,
            depthPitch,
            _layerPitch);
    }

    private static void GetMipDimensions(
        uint width,
        uint height,
        uint depth,
        uint mipLevel,
        out uint mipWidth,
        out uint mipHeight,
        out uint mipDepth)
    {
        mipWidth = Util.GetDimension(width, mipLevel);
        mipHeight = Util.GetDimension(height, mipLevel);
        mipDepth = Util.GetDimension(depth, mipLevel);
    }

    private static NeoVeldridException LayoutTooLarge(
        Exception innerException = null) =>
        new NeoVeldridException(
            "The staging texture layout exceeds NeoVeldrid's UInt32 byte-addressable mapping limit.",
            innerException);
}

internal readonly struct StagingTextureSubresourceLayout
{
    internal uint Offset { get; }
    internal uint SizeInBytes { get; }
    internal uint RowPitch { get; }
    internal uint DepthPitch { get; }
    internal uint ArrayPitch { get; }

    internal StagingTextureSubresourceLayout(
        uint offset,
        uint sizeInBytes,
        uint rowPitch,
        uint depthPitch,
        uint arrayPitch)
    {
        Offset = offset;
        SizeInBytes = sizeInBytes;
        RowPitch = rowPitch;
        DepthPitch = depthPitch;
        ArrayPitch = arrayPitch;
    }
}
