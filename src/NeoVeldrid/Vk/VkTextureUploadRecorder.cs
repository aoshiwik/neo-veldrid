using Silk.NET.Vulkan;
using VkApi = Silk.NET.Vulkan.Vk;

namespace NeoVeldrid.Vk;

/// <summary>
/// Owns the Vulkan image-transfer access contract and records tightly-packed
/// color uploads. Layout tracking alone is insufficient when consecutive
/// writes retain TransferDstOptimal, so repeated transfer writes receive an
/// exact write-after-write dependency here. All paths restore the same
/// usage-driven canonical post-transfer layouts.
/// </summary>
internal static unsafe class VkTextureUploadRecorder
{
    public static void Record(
        VkGraphicsDevice graphicsDevice,
        CommandBuffer commandBuffer,
        VkBuffer source,
        uint sourceOffset,
        uint sourceSizeInBytes,
        VkTexture destination,
        uint x,
        uint y,
        uint z,
        uint width,
        uint height,
        uint depth,
        uint mipLevel,
        uint arrayLayer,
        VkImageLayoutTransaction transaction)
    {
        ImageLayoutSnapshot originalLayouts = PrepareImageForTransferWrite(
            graphicsDevice.Vk,
            commandBuffer,
            destination,
            mipLevel,
            1,
            arrayLayer,
            1,
            transaction);

        ImageSubresourceLayers destinationSubresource =
            new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = 1,
                MipLevel = mipLevel,
                BaseArrayLayer = arrayLayer
            };
        BufferImageCopy region = new BufferImageCopy
        {
            BufferOffset = sourceOffset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageOffset = new Offset3D
            {
                X = checked((int)x),
                Y = checked((int)y),
                Z = checked((int)z)
            },
            ImageExtent = new Extent3D
            {
                Width = width,
                Height = height,
                Depth = depth
            },
            ImageSubresource = destinationSubresource
        };
        VkBufferTransferAccess.BeginTransferRead(
            graphicsDevice.Vk,
            commandBuffer,
            source.DeviceBuffer,
            sourceOffset,
            sourceSizeInBytes);
        graphicsDevice.Vk.CmdCopyBufferToImage(
            commandBuffer,
            source.DeviceBuffer,
            destination.OptimalDeviceImage,
            ImageLayout.TransferDstOptimal,
            1,
            in region);
        VkBufferTransferAccess.EndTransferRead(
            graphicsDevice.Vk,
            commandBuffer,
            source.DeviceBuffer,
            sourceOffset,
            sourceSizeInBytes);

        RestoreImageAfterTransferWrite(
            commandBuffer,
            originalLayouts,
            transaction);
    }

    internal static uint GetRequiredStagingAlignment(PixelFormat format)
        => FormatHelpers.IsCompressedFormat(format)
            ? FormatHelpers.GetBlockSizeInBytes(format)
            : FormatSizeHelpers.GetSizeInBytes(format);

    internal static ImageLayoutSnapshot PrepareImageForTransferRead(
        CommandBuffer commandBuffer,
        VkTexture texture,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        VkImageLayoutTransaction transaction)
    {
        ImageLayoutSnapshot originalLayouts = CaptureLayouts(
            texture,
            baseMipLevel,
            levelCount,
            baseArrayLayer,
            layerCount,
            transaction);
        TransitionRange(
            commandBuffer,
            originalLayouts,
            ImageLayout.TransferSrcOptimal,
            transaction);
        return originalLayouts;
    }

    internal static ImageLayoutSnapshot PrepareImageForTransferWrite(
        VkApi vk,
        CommandBuffer commandBuffer,
        VkTexture texture,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        VkImageLayoutTransaction transaction)
    {
        ImageLayoutSnapshot originalLayouts = CaptureLayouts(
            texture,
            baseMipLevel,
            levelCount,
            baseArrayLayer,
            layerCount,
            transaction);
        ImageLayout firstLayout = originalLayouts.Layouts[0];
        bool allAlreadyTransferDestinations = true;
        bool homogeneousRange = true;
        for (int i = 0; i < originalLayouts.Layouts.Length; i++)
        {
            ImageLayout currentLayout = originalLayouts.Layouts[i];
            if (currentLayout != ImageLayout.TransferDstOptimal)
                allAlreadyTransferDestinations = false;
            if (currentLayout != firstLayout)
                homogeneousRange = false;
        }

        if (allAlreadyTransferDestinations)
        {
            RecordRepeatedTransferWriteBarrier(
                vk,
                commandBuffer,
                texture,
                baseMipLevel,
                levelCount,
                baseArrayLayer,
                layerCount);
            return originalLayouts;
        }

        if (homogeneousRange)
        {
            texture.TransitionImageLayout(
                commandBuffer,
                baseMipLevel,
                levelCount,
                baseArrayLayer,
                layerCount,
                ImageLayout.TransferDstOptimal,
                transaction);
            return originalLayouts;
        }

        // Mixed ranges require exact same-layout barriers for the subresources
        // which are already transfer destinations. The remaining subresources
        // are transitioned independently by the texture's nonmatching path.
        for (uint level = 0; level < levelCount; level++)
        {
            for (uint layer = 0; layer < layerCount; layer++)
            {
                uint mipLevel = baseMipLevel + level;
                uint arrayLayer = baseArrayLayer + layer;
                if (originalLayouts.GetLayout(level, layer) ==
                    ImageLayout.TransferDstOptimal)
                {
                    RecordRepeatedTransferWriteBarrier(
                        vk,
                        commandBuffer,
                        texture,
                        mipLevel,
                        1,
                        arrayLayer,
                        1);
                }
            }
        }

        texture.TransitionImageLayoutNonmatching(
            commandBuffer,
            baseMipLevel,
            levelCount,
            baseArrayLayer,
            layerCount,
            ImageLayout.TransferDstOptimal,
            transaction);
        return originalLayouts;
    }

    internal static void RestoreImageAfterTransferRead(
        CommandBuffer commandBuffer,
        ImageLayoutSnapshot originalLayouts,
        VkImageLayoutTransaction transaction)
    {
        RestoreLayouts(
            commandBuffer,
            originalLayouts,
            ImageLayout.TransferSrcOptimal,
            transaction);
    }

    internal static void RestoreImageAfterTransferWrite(
        CommandBuffer commandBuffer,
        ImageLayoutSnapshot originalLayouts,
        VkImageLayoutTransaction transaction)
    {
        RestoreLayouts(
            commandBuffer,
            originalLayouts,
            ImageLayout.TransferDstOptimal,
            transaction);
    }

    private static ImageLayout GetInitialPostTransferLayout(
        TextureUsage usage,
        ImageLayout transferOnlyLayout)
    {
        if ((usage & TextureUsage.Storage) != 0)
            return ImageLayout.General;
        if ((usage & TextureUsage.Sampled) != 0)
            return ImageLayout.ShaderReadOnlyOptimal;
        if ((usage & TextureUsage.DepthStencil) != 0)
            return ImageLayout.DepthStencilAttachmentOptimal;
        if ((usage & TextureUsage.RenderTarget) != 0)
            return ImageLayout.ColorAttachmentOptimal;

        return transferOnlyLayout;
    }

    private static void RestoreLayouts(
        CommandBuffer commandBuffer,
        ImageLayoutSnapshot originalLayouts,
        ImageLayout transferOnlyLayout,
        VkImageLayoutTransaction transaction)
    {
        VkTexture texture = originalLayouts.Texture;
        for (uint level = 0; level < originalLayouts.LevelCount; level++)
        {
            for (uint layer = 0; layer < originalLayouts.LayerCount; layer++)
            {
                ImageLayout originalLayout = originalLayouts.GetLayout(level, layer);
                ImageLayout restoredLayout =
                    originalLayout == ImageLayout.Undefined ||
                    originalLayout == ImageLayout.Preinitialized
                        ? GetInitialPostTransferLayout(texture.Usage, transferOnlyLayout)
                        : originalLayout;
                texture.TransitionImageLayout(
                    commandBuffer,
                    originalLayouts.BaseMipLevel + level,
                    1,
                    originalLayouts.BaseArrayLayer + layer,
                    1,
                    restoredLayout,
                    transaction);
            }
        }
    }

    private static void TransitionRange(
        CommandBuffer commandBuffer,
        ImageLayoutSnapshot originalLayouts,
        ImageLayout newLayout,
        VkImageLayoutTransaction transaction)
    {
        VkTexture texture = originalLayouts.Texture;
        ImageLayout firstLayout = originalLayouts.Layouts[0];
        bool homogeneousRange = true;
        for (int i = 1; i < originalLayouts.Layouts.Length; i++)
        {
            if (originalLayouts.Layouts[i] != firstLayout)
            {
                homogeneousRange = false;
                break;
            }
        }

        if (homogeneousRange)
        {
            texture.TransitionImageLayout(
                commandBuffer,
                originalLayouts.BaseMipLevel,
                originalLayouts.LevelCount,
                originalLayouts.BaseArrayLayer,
                originalLayouts.LayerCount,
                newLayout,
                transaction);
        }
        else
        {
            texture.TransitionImageLayoutNonmatching(
                commandBuffer,
                originalLayouts.BaseMipLevel,
                originalLayouts.LevelCount,
                originalLayouts.BaseArrayLayer,
                originalLayouts.LayerCount,
                newLayout,
                transaction);
        }
    }

    private static ImageLayoutSnapshot CaptureLayouts(
        VkTexture texture,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        VkImageLayoutTransaction transaction)
    {
        var layouts = new ImageLayout[checked((int)(levelCount * layerCount))];
        int index = 0;
        for (uint level = 0; level < levelCount; level++)
        {
            for (uint layer = 0; layer < layerCount; layer++)
            {
                layouts[index++] = texture.GetImageLayout(
                    baseMipLevel + level,
                    baseArrayLayer + layer,
                    transaction);
            }
        }

        return new ImageLayoutSnapshot(
            texture,
            baseMipLevel,
            levelCount,
            baseArrayLayer,
            layerCount,
            layouts);
    }

    internal readonly record struct ImageLayoutSnapshot(
        VkTexture Texture,
        uint BaseMipLevel,
        uint LevelCount,
        uint BaseArrayLayer,
        uint LayerCount,
        ImageLayout[] Layouts)
    {
        internal ImageLayout GetLayout(uint relativeMipLevel, uint relativeArrayLayer)
            => Layouts[checked((int)(relativeMipLevel * LayerCount + relativeArrayLayer))];
    }

    private static void RecordRepeatedTransferWriteBarrier(
        VkApi vk,
        CommandBuffer commandBuffer,
        VkTexture destination,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount)
    {
        ImageMemoryBarrier barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = VkApi.QueueFamilyIgnored,
            DstQueueFamilyIndex = VkApi.QueueFamilyIgnored,
            Image = destination.OptimalDeviceImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = destination.ImageAspectMask,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                BaseArrayLayer = baseArrayLayer,
                LayerCount = layerCount
            }
        };
        vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }

}
