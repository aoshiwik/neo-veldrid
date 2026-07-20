namespace NeoVeldrid;

/// <summary>
/// Calculates the tightly packed texel-block payload implied by a complete texture description. Native image memory can
/// be larger because of implementation-defined alignment and metadata, so this is a lower bound rather than an allocation
/// size prediction.
/// </summary>
internal static class TextureStorageFootprint
{
    /// <summary>
    /// Tries to calculate the lower-bound byte footprint of all mip levels, physical array layers, and samples. A false
    /// result means that the exact footprint exceeds <see cref="System.UInt64.MaxValue"/>.
    /// </summary>
    internal static bool TryCalculateMinimumSizeInBytes(
        in TextureDescription description,
        out ulong sizeInBytes)
    {
        GetTexelBlockShape(
            description.Format,
            out uint blockWidth,
            out uint blockHeight,
            out uint blockSizeInBytes);

        ulong perLayerSize = 0;
        for (uint mipLevel = 0; mipLevel < description.MipLevels; mipLevel++)
        {
            ulong widthInBlocks = DivideRoundUp(
                Util.GetDimension(description.Width, mipLevel),
                blockWidth);
            ulong heightInBlocks = DivideRoundUp(
                Util.GetDimension(description.Height, mipLevel),
                blockHeight);
            ulong depth = Util.GetDimension(description.Depth, mipLevel);

            if (!TryMultiply(widthInBlocks, heightInBlocks, out ulong mipSize)
                || !TryMultiply(mipSize, depth, out mipSize)
                || !TryMultiply(mipSize, blockSizeInBytes, out mipSize)
                || !TryAdd(perLayerSize, mipSize, out perLayerSize))
            {
                sizeInBytes = 0;
                return false;
            }
        }

        ulong physicalArrayLayers = description.ArrayLayers;
        if ((description.Usage & TextureUsage.Cubemap) != 0)
        {
            if (!TryMultiply(physicalArrayLayers, 6, out physicalArrayLayers))
            {
                sizeInBytes = 0;
                return false;
            }
        }

        if (!TryMultiply(perLayerSize, physicalArrayLayers, out sizeInBytes)
            || !TryMultiply(
                sizeInBytes,
                FormatHelpers.GetSampleCountUInt32(description.SampleCount),
                out sizeInBytes))
        {
            sizeInBytes = 0;
            return false;
        }

        return true;
    }

    private static void GetTexelBlockShape(
        PixelFormat format,
        out uint blockWidth,
        out uint blockHeight,
        out uint blockSizeInBytes)
    {
        if (FormatHelpers.IsCompressedFormat(format))
        {
            blockWidth = 4;
            blockHeight = 4;
            blockSizeInBytes = FormatHelpers.GetBlockSizeInBytes(format);
            return;
        }

        blockWidth = 1;
        blockHeight = 1;
        // FormatSizeHelpers follows Vulkan's defined texel-block sizes. In
        // particular, D32_Float_S8_UInt has a five-byte texel block; a Vulkan
        // implementation may add 24 unused bits, but they are optional and
        // therefore cannot be part of this guaranteed lower bound.
        blockSizeInBytes = FormatSizeHelpers.GetSizeInBytes(format);
    }

    private static ulong DivideRoundUp(uint value, uint divisor) =>
        ((ulong)value + divisor - 1u) / divisor;

    private static bool TryAdd(
        ulong left,
        ulong right,
        out ulong result)
    {
        if (right > ulong.MaxValue - left)
        {
            result = 0;
            return false;
        }

        result = left + right;
        return true;
    }

    private static bool TryMultiply(
        ulong left,
        ulong right,
        out ulong result)
    {
        if (left != 0 && right > ulong.MaxValue / left)
        {
            result = 0;
            return false;
        }

        result = left * right;
        return true;
    }
}
