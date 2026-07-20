using System;

namespace NeoVeldrid;

/// <summary>
/// Owns the backend-independent invariants of <see cref="TextureDescription"/>.
/// </summary>
internal static class TextureDescriptionValidation
{
    private const TextureUsage AllTextureUsages =
        TextureUsage.Sampled
        | TextureUsage.Storage
        | TextureUsage.RenderTarget
        | TextureUsage.DepthStencil
        | TextureUsage.Cubemap
        | TextureUsage.Staging
        | TextureUsage.GenerateMipmaps;

    internal static TextureSupportResult Query(in TextureDescription description)
    {
        if (!Enum.IsDefined(description.Format))
            return Invalid(TextureSupportReason.UnknownPixelFormat);
        if (!Enum.IsDefined(description.Type))
            return Invalid(TextureSupportReason.UnknownTextureType);
        if (!Enum.IsDefined(description.SampleCount))
            return Invalid(TextureSupportReason.UnknownSampleCount);
        if ((description.Usage & ~AllTextureUsages) != 0)
            return Invalid(TextureSupportReason.UnknownTextureUsage);
        if (description.Width == 0 || description.Height == 0 || description.Depth == 0)
            return Invalid(TextureSupportReason.ZeroExtent);
        if (description.MipLevels == 0)
            return Invalid(TextureSupportReason.ZeroMipLevels);
        if (description.ArrayLayers == 0)
            return Invalid(TextureSupportReason.ZeroArrayLayers);

        switch (description.Type)
        {
            case TextureType.Texture1D:
                if (description.Height != 1 || description.Depth != 1)
                    return Invalid(TextureSupportReason.Texture1DShape);
                break;
            case TextureType.Texture2D:
                if (description.Depth != 1)
                    return Invalid(TextureSupportReason.Texture2DShape);
                break;
            case TextureType.Texture3D:
                if (description.ArrayLayers != 1)
                    return Invalid(TextureSupportReason.Texture3DArrayLayers);
                break;
        }

        bool isCubemap = (description.Usage & TextureUsage.Cubemap) != 0;
        if (isCubemap)
        {
            if (description.Type != TextureType.Texture2D)
                return Invalid(TextureSupportReason.CubemapTextureType);
            if (description.Width != description.Height)
                return Invalid(TextureSupportReason.CubemapSquareFaces);
            if (description.SampleCount != TextureSampleCount.Count1)
                return Invalid(TextureSupportReason.CubemapMultisample);

            try
            {
                _ = checked(description.ArrayLayers * 6u);
            }
            catch (OverflowException)
            {
                return Invalid(TextureSupportReason.EffectiveArrayLayerOverflow);
            }
        }

        bool isStaging = (description.Usage & TextureUsage.Staging) != 0;
        if (isStaging && description.Usage != TextureUsage.Staging)
            return Invalid(TextureSupportReason.StagingUsageCombination);
        if (isStaging && description.SampleCount != TextureSampleCount.Count1)
            return LibraryContract(TextureSupportReason.StagingMultisample);

        bool isDepthStencil =
            (description.Usage & TextureUsage.DepthStencil) != 0;
        if (isDepthStencil
            && !FormatHelpers.IsDepthStencilFormat(description.Format))
        {
            return LibraryContract(TextureSupportReason.DepthStencilFormat);
        }

        if (FormatHelpers.IsStencilFormat(description.Format))
        {
            if (isStaging)
                return LibraryContract(TextureSupportReason.PackedDepthStencilStaging);
            if (!isDepthStencil)
                return LibraryContract(TextureSupportReason.PackedDepthStencilUsage);
        }

        if (isDepthStencil
            && (description.Usage & TextureUsage.GenerateMipmaps) != 0)
        {
            return LibraryContract(TextureSupportReason.DepthStencilMipGeneration);
        }

        if (description.SampleCount != TextureSampleCount.Count1)
        {
            if (description.Type != TextureType.Texture2D)
                return Invalid(TextureSupportReason.MultisampleTextureType);
            if (description.MipLevels != 1)
                return Invalid(TextureSupportReason.MultisampleMipLevels);
        }

        uint maxLogicalMipLevels = GetLogicalMipLevelCount(
            description.Width,
            description.Height,
            description.Depth);
        if (description.MipLevels > maxLogicalMipLevels)
            return Invalid(TextureSupportReason.LogicalMipChain);

        if (isStaging)
        {
            try
            {
                _ = TextureStagingLayout.Create(description);
            }
            catch (NeoVeldridException)
            {
                return LibraryContract(TextureSupportReason.StagingAddressLimit);
            }
        }

        return new TextureSupportResult(
            TextureSupportClassification.Supported,
            TextureSupportReason.None);
    }

    internal static string GetFailureMessage(TextureSupportReason reason) =>
        reason switch
        {
            TextureSupportReason.UnknownPixelFormat => "The texture format is not a defined PixelFormat value.",
            TextureSupportReason.UnknownTextureType => "The texture type is not a defined TextureType value.",
            TextureSupportReason.UnknownTextureUsage => "The texture usage contains an undefined TextureUsage flag.",
            TextureSupportReason.UnknownSampleCount => "The texture sample count is not a defined TextureSampleCount value.",
            TextureSupportReason.ZeroExtent => "Width, Height, and Depth must be non-zero.",
            TextureSupportReason.ZeroMipLevels => "MipLevels must be non-zero.",
            TextureSupportReason.ZeroArrayLayers => "ArrayLayers must be non-zero.",
            TextureSupportReason.Texture1DShape => "A 1D texture must have Height and Depth equal to one.",
            TextureSupportReason.Texture2DShape => "A 2D texture must have Depth equal to one.",
            TextureSupportReason.Texture3DArrayLayers => "A 3D texture must have exactly one array layer.",
            TextureSupportReason.CubemapTextureType => "A cubemap must be a 2D texture.",
            TextureSupportReason.CubemapSquareFaces => "Cubemap faces must be square.",
            TextureSupportReason.CubemapMultisample => "Cubemaps cannot be multisampled.",
            TextureSupportReason.StagingUsageCombination => "TextureUsage.Staging cannot be combined with any other flags.",
            TextureSupportReason.StagingMultisample => "A staging texture must use TextureSampleCount.Count1 because its mapped layout has no sample dimension.",
            TextureSupportReason.MultisampleTextureType => "Only 2D textures can be multisampled.",
            TextureSupportReason.MultisampleMipLevels => "A multisampled texture must have exactly one mip level.",
            TextureSupportReason.LogicalMipChain => "MipLevels exceeds the complete logical mip chain for the texture dimensions.",
            TextureSupportReason.EffectiveArrayLayerOverflow => "The cubemap face count exceeds UInt32.MaxValue.",
            TextureSupportReason.DepthStencilFormat =>
                "TextureUsage.DepthStencil requires a depth-stencil format.",
            TextureSupportReason.PackedDepthStencilUsage => "Packed depth-stencil formats require TextureUsage.DepthStencil.",
            TextureSupportReason.PackedDepthStencilStaging =>
                "NeoVeldrid staging textures do not define a packed depth-stencil plane layout. Use a depth-only format or an aspect-explicit transfer API.",
            TextureSupportReason.DepthStencilMipGeneration =>
                "TextureUsage.DepthStencil and TextureUsage.GenerateMipmaps cannot be combined.",
            TextureSupportReason.StagingAddressLimit =>
                "The staging texture layout exceeds NeoVeldrid's UInt32 byte-addressable mapping limit.",
            _ => $"The texture description is invalid: {reason}.",
        };

    private static uint GetLogicalMipLevelCount(uint width, uint height, uint depth)
    {
        uint dimension = Math.Max(width, Math.Max(height, depth));
        uint mipLevels = 0;
        do
        {
            mipLevels++;
            dimension >>= 1;
        }
        while (dimension != 0);

        return mipLevels;
    }

    private static TextureSupportResult Invalid(TextureSupportReason reason) =>
        TextureSupportResult.Unsupported(
            TextureSupportClassification.InvalidDescription,
            reason);

    private static TextureSupportResult LibraryContract(TextureSupportReason reason) =>
        TextureSupportResult.Unsupported(
            TextureSupportClassification.LibraryContract,
            reason);
}
