using Silk.NET.Vulkan;
using VkApi = Silk.NET.Vulkan.Vk;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace NeoVeldrid.Vk;

/// <summary>
/// Records memory dependencies for buffer-backed transfer storage, including
/// command-list upload pages and staging textures. These buffers can alternate
/// between direct host access and transfer reads or writes, and Vulkan does not
/// infer those dependencies from queue order or from a paired image's layout
/// transitions.
/// </summary>
internal static unsafe class VkBufferTransferAccess
{
    private const PipelineStageFlags HostAndTransferStages =
        PipelineStageFlags.HostBit |
        PipelineStageFlags.TransferBit;

    private const AccessFlags HostAndTransferAccess =
        AccessFlags.HostReadBit |
        AccessFlags.HostWriteBit |
        AccessFlags.TransferReadBit |
        AccessFlags.TransferWriteBit;

    public static void BeginTransferRead(
        VkApi vk,
        CommandBuffer commandBuffer,
        VkBufferHandle buffer,
        ulong offset = 0,
        ulong size = ulong.MaxValue)
        => RecordBarrier(
            vk,
            commandBuffer,
            buffer,
            offset,
            size,
            HostAndTransferStages,
            PipelineStageFlags.TransferBit,
            HostAndTransferAccess,
            AccessFlags.TransferReadBit);

    public static void EndTransferRead(
        VkApi vk,
        CommandBuffer commandBuffer,
        VkBufferHandle buffer,
        ulong offset = 0,
        ulong size = ulong.MaxValue)
        => RecordBarrier(
            vk,
            commandBuffer,
            buffer,
            offset,
            size,
            PipelineStageFlags.TransferBit,
            HostAndTransferStages,
            AccessFlags.TransferReadBit,
            HostAndTransferAccess);

    public static void BeginTransferWrite(
        VkApi vk,
        CommandBuffer commandBuffer,
        VkBufferHandle buffer,
        ulong offset = 0,
        ulong size = ulong.MaxValue)
        => RecordBarrier(
            vk,
            commandBuffer,
            buffer,
            offset,
            size,
            HostAndTransferStages,
            PipelineStageFlags.TransferBit,
            HostAndTransferAccess,
            AccessFlags.TransferWriteBit);

    public static void EndTransferWrite(
        VkApi vk,
        CommandBuffer commandBuffer,
        VkBufferHandle buffer,
        ulong offset = 0,
        ulong size = ulong.MaxValue)
        => RecordBarrier(
            vk,
            commandBuffer,
            buffer,
            offset,
            size,
            PipelineStageFlags.TransferBit,
            HostAndTransferStages,
            AccessFlags.TransferWriteBit,
            HostAndTransferAccess);

    private static void RecordBarrier(
        VkApi vk,
        CommandBuffer commandBuffer,
        VkBufferHandle buffer,
        ulong offset,
        ulong size,
        PipelineStageFlags sourceStages,
        PipelineStageFlags destinationStages,
        AccessFlags sourceAccess,
        AccessFlags destinationAccess)
    {
        BufferMemoryBarrier barrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess,
            SrcQueueFamilyIndex = VkApi.QueueFamilyIgnored,
            DstQueueFamilyIndex = VkApi.QueueFamilyIgnored,
            Buffer = buffer,
            Offset = offset,
            Size = size
        };

        vk.CmdPipelineBarrier(
            commandBuffer,
            sourceStages,
            destinationStages,
            0,
            0,
            null,
            1,
            &barrier,
            0,
            null);
    }
}
