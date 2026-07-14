#if TEST_VULKAN
using NeoVeldrid.Vk;
using Silk.NET.Vulkan;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanBufferCopyBarrierTests
{
    [Theory]
    [InlineData(
        BufferUsage.UniformBuffer,
        AccessFlags.UniformReadBit,
        PipelineStageFlags.AllCommandsBit)]
    [InlineData(
        BufferUsage.VertexBuffer,
        AccessFlags.VertexAttributeReadBit,
        PipelineStageFlags.VertexInputBit)]
    [InlineData(
        BufferUsage.IndexBuffer,
        AccessFlags.IndexReadBit,
        PipelineStageFlags.VertexInputBit)]
    [InlineData(
        BufferUsage.IndirectBuffer,
        AccessFlags.IndirectCommandReadBit,
        PipelineStageFlags.DrawIndirectBit)]
    public void ReadConsumersParticipateInTheirPipelineAccess(
        BufferUsage usage,
        AccessFlags expectedAccess,
        PipelineStageFlags expectedStage)
    {
        VkBufferCopyAccessContract contract = VkBufferCopyRecorder.DescribeUsage(usage);

        AssertIncludes(contract.Access, expectedAccess);
        AssertIncludes(contract.Stages, expectedStage);
    }

    [Theory]
    [InlineData(BufferUsage.StructuredBufferReadOnly, false)]
    [InlineData(BufferUsage.StructuredBufferReadWrite, true)]
    public void StructuredBuffersParticipateInShaderAccess(
        BufferUsage usage,
        bool expectsShaderWrite)
    {
        VkBufferCopyAccessContract contract = VkBufferCopyRecorder.DescribeUsage(usage);

        AssertIncludes(contract.Access, AccessFlags.ShaderReadBit);
        AssertIncludes(contract.Stages, PipelineStageFlags.AllCommandsBit);

        if (expectsShaderWrite)
            AssertIncludes(contract.Access, AccessFlags.ShaderWriteBit);
        else
            AssertExcludes(contract.Access, AccessFlags.ShaderWriteBit);
    }

    [Fact]
    public void IndirectBuffersParticipateInDrawIndirectReads()
    {
        VkBufferCopyAccessContract contract =
            VkBufferCopyRecorder.DescribeUsage(BufferUsage.IndirectBuffer);

        AssertIncludes(contract.Access, AccessFlags.IndirectCommandReadBit);
        AssertIncludes(contract.Stages, PipelineStageFlags.DrawIndirectBit);
    }

    [Theory]
    [InlineData(BufferUsage.Dynamic)]
    [InlineData(BufferUsage.Staging)]
    public void HostVisibleBuffersParticipateInHostReadsAndWrites(BufferUsage usage)
    {
        VkBufferCopyAccessContract contract = VkBufferCopyRecorder.DescribeUsage(usage);

        AssertIncludes(
            contract.Access,
            AccessFlags.HostReadBit | AccessFlags.HostWriteBit);
        AssertIncludes(contract.Stages, PipelineStageFlags.HostBit);
    }

    [Fact]
    public void DynamicUniformBufferUnionsHostAndShaderConsumption()
    {
        VkBufferCopyAccessContract contract = VkBufferCopyRecorder.DescribeUsage(
            BufferUsage.UniformBuffer | BufferUsage.Dynamic);

        AssertIncludes(
            contract.Access,
            AccessFlags.UniformReadBit |
            AccessFlags.HostReadBit |
            AccessFlags.HostWriteBit);
        AssertIncludes(
            contract.Stages,
            PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit);
    }

    [Fact]
    public void CombinedConsumerUsagesProduceOneUnionedContract()
    {
        BufferUsage usage =
            BufferUsage.StructuredBufferReadWrite |
            BufferUsage.IndirectBuffer |
            BufferUsage.VertexBuffer;

        VkBufferCopyAccessContract contract = VkBufferCopyRecorder.DescribeUsage(usage);

        AssertIncludes(
            contract.Access,
            AccessFlags.ShaderReadBit |
            AccessFlags.ShaderWriteBit |
            AccessFlags.IndirectCommandReadBit |
            AccessFlags.VertexAttributeReadBit);
        AssertIncludes(
            contract.Stages,
            PipelineStageFlags.AllCommandsBit |
            PipelineStageFlags.DrawIndirectBit |
            PipelineStageFlags.VertexInputBit);
    }

    [Theory]
    [InlineData((BufferUsage)0)]
    [InlineData(BufferUsage.VertexBuffer)]
    [InlineData(BufferUsage.IndexBuffer)]
    [InlineData(BufferUsage.UniformBuffer)]
    [InlineData(BufferUsage.StructuredBufferReadOnly)]
    [InlineData(BufferUsage.StructuredBufferReadWrite)]
    [InlineData(BufferUsage.IndirectBuffer)]
    [InlineData(BufferUsage.Dynamic)]
    [InlineData(BufferUsage.Staging)]
    public void EveryBufferContractIncludesTransferHazards(BufferUsage usage)
    {
        VkBufferCopyAccessContract contract = VkBufferCopyRecorder.DescribeUsage(usage);

        AssertIncludes(
            contract.Access,
            AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit);
        AssertIncludes(contract.Stages, PipelineStageFlags.TransferBit);
    }

    private static void AssertIncludes(AccessFlags actual, AccessFlags expected)
        => Assert.Equal(expected, actual & expected);

    private static void AssertExcludes(AccessFlags actual, AccessFlags unexpected)
        => Assert.Equal(default(AccessFlags), actual & unexpected);

    private static void AssertIncludes(PipelineStageFlags actual, PipelineStageFlags expected)
        => Assert.Equal(expected, actual & expected);
}
#endif
