using Silk.NET.Vulkan;
using VkApi = Silk.NET.Vulkan.Vk;

namespace NeoVeldrid.Vk;

/// <summary>
/// Records a buffer copy with conservative source and destination transitions.
/// Every Vulkan buffer is created with transfer-source and transfer-destination
/// usage, so transfer hazards belong to every buffer's access contract.
/// </summary>
internal static unsafe class VkBufferCopyRecorder
{
    public static void Record(
        VkGraphicsDevice graphicsDevice,
        CommandBuffer commandBuffer,
        VkBuffer source,
        uint sourceOffset,
        VkBuffer destination,
        uint destinationOffset,
        uint sizeInBytes)
    {
        VkBufferCopyAccessContract sourceContract = DescribeUsage(source.Usage);
        VkBufferCopyAccessContract destinationContract = DescribeUsage(destination.Usage);

        BufferMemoryBarrier* beforeCopy = stackalloc BufferMemoryBarrier[2];
        beforeCopy[0] = CreateBarrier(
            source,
            sourceOffset,
            sizeInBytes,
            sourceContract.Access,
            AccessFlags.TransferReadBit);
        beforeCopy[1] = CreateBarrier(
            destination,
            destinationOffset,
            sizeInBytes,
            destinationContract.Access,
            AccessFlags.TransferWriteBit);

        graphicsDevice.Vk.CmdPipelineBarrier(
            commandBuffer,
            sourceContract.Stages | destinationContract.Stages,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            2,
            beforeCopy,
            0,
            null);

        BufferCopy copy = new BufferCopy
        {
            SrcOffset = sourceOffset,
            DstOffset = destinationOffset,
            Size = sizeInBytes
        };
        graphicsDevice.Vk.CmdCopyBuffer(
            commandBuffer,
            source.DeviceBuffer,
            destination.DeviceBuffer,
            1,
            in copy);

        BufferMemoryBarrier* afterCopy = stackalloc BufferMemoryBarrier[2];
        afterCopy[0] = CreateBarrier(
            source,
            sourceOffset,
            sizeInBytes,
            AccessFlags.TransferReadBit,
            sourceContract.Access);
        afterCopy[1] = CreateBarrier(
            destination,
            destinationOffset,
            sizeInBytes,
            AccessFlags.TransferWriteBit,
            destinationContract.Access);

        graphicsDevice.Vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            sourceContract.Stages | destinationContract.Stages,
            0,
            0,
            null,
            2,
            afterCopy,
            0,
            null);
    }

    private static BufferMemoryBarrier CreateBarrier(
        VkBuffer buffer,
        uint offset,
        uint sizeInBytes,
        AccessFlags sourceAccess,
        AccessFlags destinationAccess)
        => new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess,
            SrcQueueFamilyIndex = VkApi.QueueFamilyIgnored,
            DstQueueFamilyIndex = VkApi.QueueFamilyIgnored,
            Buffer = buffer.DeviceBuffer,
            Offset = offset,
            Size = sizeInBytes
        };

    /// <summary>
    /// Describes every pipeline access which can legally precede or follow a
    /// transfer for a buffer with the supplied usage. The result is deliberately
    /// conservative because NeoVeldrid does not track the last individual use of
    /// a buffer between command lists.
    /// </summary>
    internal static VkBufferCopyAccessContract DescribeUsage(BufferUsage usage)
    {
        AccessFlags access = AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit;
        PipelineStageFlags stages = PipelineStageFlags.TransferBit;

        if ((usage & BufferUsage.UniformBuffer) != 0)
        {
            access |= AccessFlags.UniformReadBit;
            stages |= PipelineStageFlags.AllCommandsBit;
        }
        if ((usage & BufferUsage.VertexBuffer) != 0)
        {
            access |= AccessFlags.VertexAttributeReadBit;
            stages |= PipelineStageFlags.VertexInputBit;
        }
        if ((usage & BufferUsage.IndexBuffer) != 0)
        {
            access |= AccessFlags.IndexReadBit;
            stages |= PipelineStageFlags.VertexInputBit;
        }
        if ((usage & BufferUsage.IndirectBuffer) != 0)
        {
            access |= AccessFlags.IndirectCommandReadBit;
            stages |= PipelineStageFlags.DrawIndirectBit;
        }
        if ((usage & (BufferUsage.StructuredBufferReadOnly | BufferUsage.StructuredBufferReadWrite)) != 0)
        {
            access |= AccessFlags.ShaderReadBit;
            if ((usage & BufferUsage.StructuredBufferReadWrite) != 0)
                access |= AccessFlags.ShaderWriteBit;
            stages |= PipelineStageFlags.AllCommandsBit;
        }
        if ((usage & (BufferUsage.Dynamic | BufferUsage.Staging)) != 0)
        {
            access |= AccessFlags.HostReadBit | AccessFlags.HostWriteBit;
            stages |= PipelineStageFlags.HostBit;
        }

        return new VkBufferCopyAccessContract(access, stages);
    }
}

/// <summary>
/// The access masks and pipeline stages participating in a Vulkan buffer's
/// transfer synchronization contract.
/// </summary>
internal readonly record struct VkBufferCopyAccessContract(
    AccessFlags Access,
    PipelineStageFlags Stages);
