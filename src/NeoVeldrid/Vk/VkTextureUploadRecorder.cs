using Silk.NET.Vulkan;

namespace NeoVeldrid.Vk;

/// <summary>
/// Records tightly-packed command-list texture uploads. Image layout
/// ownership is delegated to <see cref="VkImageTransferLayout"/>, the shared
/// authority used by every Vulkan image-transfer path.
/// </summary>
internal static class VkTextureUploadRecorder
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
        VkImageTransferLayout.Access destinationAccess =
            VkImageTransferLayout.PrepareForWrite(
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

        destinationAccess.Restore(commandBuffer, transaction);
    }

    internal static uint GetRequiredStagingAlignment(PixelFormat format)
        => FormatHelpers.IsCompressedFormat(format)
            ? FormatHelpers.GetBlockSizeInBytes(format)
            : FormatSizeHelpers.GetSizeInBytes(format);
}
