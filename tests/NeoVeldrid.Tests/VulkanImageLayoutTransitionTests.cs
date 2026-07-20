#if TEST_VULKAN
using NeoVeldrid.Vk;
using Silk.NET.Vulkan;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanImageLayoutTransitionTests
{
    private static readonly ImageLayout[] SourceLayouts =
    {
        ImageLayout.Undefined,
        ImageLayout.Preinitialized,
        ImageLayout.TransferSrcOptimal,
        ImageLayout.TransferDstOptimal,
        ImageLayout.ShaderReadOnlyOptimal,
        ImageLayout.General,
        ImageLayout.ColorAttachmentOptimal,
        ImageLayout.DepthStencilAttachmentOptimal,
        ImageLayout.PresentSrcKhr
    };

    private static readonly ImageLayout[] DestinationLayouts =
    {
        ImageLayout.TransferSrcOptimal,
        ImageLayout.TransferDstOptimal,
        ImageLayout.ShaderReadOnlyOptimal,
        ImageLayout.General,
        ImageLayout.ColorAttachmentOptimal,
        ImageLayout.DepthStencilAttachmentOptimal,
        ImageLayout.PresentSrcKhr
    };

    [Fact]
    public void EverySupportedTransitionHasNonzeroLegacyPipelineStages()
    {
        foreach (ImageLayout source in SourceLayouts)
        {
            foreach (ImageLayout destination in DestinationLayouts)
            {
                if (source == destination)
                    continue;

                VkImageLayoutTransitionContract contract =
                    VulkanUtil.DescribeImageLayoutTransition(source, destination);

                Assert.NotEqual(PipelineStageFlags.None, contract.SourceStages);
                Assert.NotEqual(PipelineStageFlags.None, contract.DestinationStages);
            }
        }
    }

    [Fact]
    public void ShaderReadLayoutCoversEveryLegalShaderConsumer()
    {
        VkImageLayoutTransitionContract beforeTransfer =
            VulkanUtil.DescribeImageLayoutTransition(
                ImageLayout.ShaderReadOnlyOptimal,
                ImageLayout.TransferDstOptimal);
        VkImageLayoutTransitionContract afterTransfer =
            VulkanUtil.DescribeImageLayoutTransition(
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal);

        Assert.Equal(
            PipelineStageFlags.AllCommandsBit,
            beforeTransfer.SourceStages);
        Assert.Equal(
            PipelineStageFlags.AllCommandsBit,
            afterTransfer.DestinationStages);
        Assert.Equal(
            AccessFlags.ShaderReadBit,
            beforeTransfer.SourceAccess);
        Assert.Equal(
            AccessFlags.ShaderReadBit,
            afterTransfer.DestinationAccess);
    }

    [Fact]
    public void AttachmentDestinationsIncludeReadWriteHazardsAndCompleteStages()
    {
        VkImageLayoutTransitionContract color =
            VulkanUtil.DescribeImageLayoutTransition(
                ImageLayout.TransferDstOptimal,
                ImageLayout.ColorAttachmentOptimal);
        VkImageLayoutTransitionContract depth =
            VulkanUtil.DescribeImageLayoutTransition(
                ImageLayout.TransferDstOptimal,
                ImageLayout.DepthStencilAttachmentOptimal);

        Assert.Equal(
            AccessFlags.ColorAttachmentReadBit |
            AccessFlags.ColorAttachmentWriteBit,
            color.DestinationAccess);
        Assert.Equal(
            AccessFlags.DepthStencilAttachmentReadBit |
            AccessFlags.DepthStencilAttachmentWriteBit,
            depth.DestinationAccess);
        Assert.Equal(
            PipelineStageFlags.EarlyFragmentTestsBit |
            PipelineStageFlags.LateFragmentTestsBit,
            depth.DestinationStages);

        SubpassDependency attachmentDependency =
            VulkanUtil.CreateRenderPassAttachmentDependency(
                hasColorAttachments: true,
                hasDepthStencilAttachment: true);

        Assert.Equal(Silk.NET.Vulkan.Vk.SubpassExternal, attachmentDependency.SrcSubpass);
        Assert.Equal(0u, attachmentDependency.DstSubpass);
        Assert.Equal(PipelineStageFlags.AllCommandsBit, attachmentDependency.SrcStageMask);
        Assert.Equal(
            PipelineStageFlags.ColorAttachmentOutputBit |
            PipelineStageFlags.EarlyFragmentTestsBit,
            attachmentDependency.DstStageMask);
        Assert.Equal(
            AccessFlags.MemoryReadBit |
            AccessFlags.MemoryWriteBit,
            attachmentDependency.SrcAccessMask);
        Assert.Equal(
            AccessFlags.ColorAttachmentReadBit |
            AccessFlags.ColorAttachmentWriteBit |
            AccessFlags.DepthStencilAttachmentReadBit |
            AccessFlags.DepthStencilAttachmentWriteBit,
            attachmentDependency.DstAccessMask);
    }

    [Fact]
    public void InvalidDestinationLayoutThrowsInsteadOfRecordingZeroMasks()
    {
        Assert.Throws<NeoVeldridException>(
            () => VulkanUtil.DescribeImageLayoutTransition(
                ImageLayout.TransferDstOptimal,
                ImageLayout.Undefined));
    }
}
#endif
