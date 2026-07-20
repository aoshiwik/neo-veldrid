using Silk.NET.Vulkan;
using VkApi = Silk.NET.Vulkan.Vk;

namespace NeoVeldrid.Vk;

/// <summary>
/// Owns temporary Vulkan image layouts for transfer commands. Every prepared
/// access captures the exact projected subresource layouts and carries the
/// matching restoration operation, so copy, upload, readback, and resolve
/// paths cannot independently invent post-transfer layout policy.
/// </summary>
internal static unsafe class VkImageTransferLayout
{
    internal static Access PrepareForRead(
        CommandBuffer commandBuffer,
        VkTexture texture,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        VkImageLayoutTransaction transaction)
    {
        Access access = Capture(
            texture,
            baseMipLevel,
            levelCount,
            baseArrayLayer,
            layerCount,
            ImageLayout.TransferSrcOptimal,
            transaction);
        TransitionRange(commandBuffer, access, transaction);
        return access;
    }

    internal static Access PrepareForWrite(
        VkApi vk,
        CommandBuffer commandBuffer,
        VkTexture texture,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        VkImageLayoutTransaction transaction)
    {
        Access access = Capture(
            texture,
            baseMipLevel,
            levelCount,
            baseArrayLayer,
            layerCount,
            ImageLayout.TransferDstOptimal,
            transaction);
        ImageLayout firstLayout = access.GetOriginalLayout(0, 0);
        bool allAlreadyTransferDestinations = true;
        bool homogeneousRange = true;
        for (uint level = 0; level < levelCount; level++)
        {
            for (uint layer = 0; layer < layerCount; layer++)
            {
                ImageLayout currentLayout = access.GetOriginalLayout(level, layer);
                if (currentLayout != ImageLayout.TransferDstOptimal)
                    allAlreadyTransferDestinations = false;
                if (currentLayout != firstLayout)
                    homogeneousRange = false;
            }
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
            return access;
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
            return access;
        }

        // Mixed ranges require exact same-layout barriers for subresources
        // which are already transfer destinations. The remaining subresources
        // are transitioned independently by the texture's nonmatching path.
        for (uint level = 0; level < levelCount; level++)
        {
            for (uint layer = 0; layer < layerCount; layer++)
            {
                if (access.GetOriginalLayout(level, layer) !=
                    ImageLayout.TransferDstOptimal)
                {
                    continue;
                }

                RecordRepeatedTransferWriteBarrier(
                    vk,
                    commandBuffer,
                    texture,
                    baseMipLevel + level,
                    1,
                    baseArrayLayer + layer,
                    1);
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
        return access;
    }

    private static Access Capture(
        VkTexture texture,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        ImageLayout transferLayout,
        VkImageLayoutTransaction transaction)
    {
        var originalLayouts =
            new ImageLayout[checked((int)(levelCount * layerCount))];
        int index = 0;
        for (uint level = 0; level < levelCount; level++)
        {
            for (uint layer = 0; layer < layerCount; layer++)
            {
                originalLayouts[index++] = texture.GetImageLayout(
                    baseMipLevel + level,
                    baseArrayLayer + layer,
                    transaction);
            }
        }

        return new Access(
            texture,
            baseMipLevel,
            levelCount,
            baseArrayLayer,
            layerCount,
            transferLayout,
            originalLayouts);
    }

    private static void TransitionRange(
        CommandBuffer commandBuffer,
        Access access,
        VkImageLayoutTransaction transaction)
    {
        ImageLayout firstLayout = access.GetOriginalLayout(0, 0);
        bool homogeneousRange = true;
        for (uint level = 0; level < access.LevelCount && homogeneousRange; level++)
        {
            for (uint layer = 0; layer < access.LayerCount; layer++)
            {
                if (access.GetOriginalLayout(level, layer) != firstLayout)
                {
                    homogeneousRange = false;
                    break;
                }
            }
        }

        if (homogeneousRange)
        {
            access.Texture.TransitionImageLayout(
                commandBuffer,
                access.BaseMipLevel,
                access.LevelCount,
                access.BaseArrayLayer,
                access.LayerCount,
                access.TransferLayout,
                transaction);
        }
        else
        {
            access.Texture.TransitionImageLayoutNonmatching(
                commandBuffer,
                access.BaseMipLevel,
                access.LevelCount,
                access.BaseArrayLayer,
                access.LayerCount,
                access.TransferLayout,
                transaction);
        }
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

    internal readonly struct Access
    {
        private readonly ImageLayout[] _originalLayouts;

        internal Access(
            VkTexture texture,
            uint baseMipLevel,
            uint levelCount,
            uint baseArrayLayer,
            uint layerCount,
            ImageLayout transferLayout,
            ImageLayout[] originalLayouts)
        {
            Texture = texture;
            BaseMipLevel = baseMipLevel;
            LevelCount = levelCount;
            BaseArrayLayer = baseArrayLayer;
            LayerCount = layerCount;
            TransferLayout = transferLayout;
            _originalLayouts = originalLayouts;
        }

        internal VkTexture Texture { get; }
        internal uint BaseMipLevel { get; }
        internal uint LevelCount { get; }
        internal uint BaseArrayLayer { get; }
        internal uint LayerCount { get; }
        internal ImageLayout TransferLayout { get; }

        internal ImageLayout GetOriginalLayout(
            uint relativeMipLevel,
            uint relativeArrayLayer)
            => _originalLayouts[checked((int)(
                relativeMipLevel * LayerCount + relativeArrayLayer))];

        internal void Restore(
            CommandBuffer commandBuffer,
            VkImageLayoutTransaction transaction)
        {
            for (uint level = 0; level < LevelCount; level++)
            {
                for (uint layer = 0; layer < LayerCount; layer++)
                {
                    ImageLayout originalLayout =
                        GetOriginalLayout(level, layer);
                    ImageLayout restoredLayout =
                        originalLayout == ImageLayout.Undefined ||
                        originalLayout == ImageLayout.Preinitialized
                            ? GetInitialPostTransferLayout(
                                Texture.Usage,
                                TransferLayout)
                            : originalLayout;
                    Texture.TransitionImageLayout(
                        commandBuffer,
                        BaseMipLevel + level,
                        1,
                        BaseArrayLayer + layer,
                        1,
                        restoredLayout,
                        transaction);
                }
            }
        }
    }
}
