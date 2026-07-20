#if TEST_VULKAN
using NeoVeldrid.Vk;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Sdk;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanRenderTargetLayoutTests
    : GraphicsDeviceTestBase<VulkanDeviceCreator>
{
    private const uint Size = 4;
    private const PixelFormat ColorFormat = PixelFormat.R8_G8_B8_A8_UNorm;

    [Fact]
    public void RenderTargetReadbackRestoresAttachmentLayoutForPreservingPass()
    {
        Texture target = CreateRenderTarget();
        VkTexture vkTarget = Assert.IsType<VkTexture>(target);
        Framebuffer framebuffer = RF.CreateFramebuffer(
            new FramebufferDescription(null, target));
        Texture capture = CreateCapture();

        CommandList clear = RF.CreateCommandList();
        clear.Begin();
        clear.SetFramebuffer(framebuffer);
        clear.ClearColorTarget(0, RgbaFloat.Red);
        clear.End();
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            vkTarget.GetImageLayout(0, 0));
        GD.SubmitCommands(clear);
        GD.WaitForIdle();
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            vkTarget.GetImageLayout(0, 0));

        CommandList readback = RF.CreateCommandList();
        readback.Begin();
        readback.CopyTexture(target, capture);
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            vkTarget.GetImageLayout(0, 0));
        readback.End();
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            vkTarget.GetImageLayout(0, 0));
        GD.SubmitCommands(readback);
        GD.WaitForIdle();
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            vkTarget.GetImageLayout(0, 0));
        AssertPixels(capture, RgbaByte.Red);

        CommandList preservingPass = RF.CreateCommandList();
        preservingPass.Begin();
        preservingPass.SetFramebuffer(framebuffer);
        preservingPass.End();
        GD.SubmitCommands(preservingPass);
        GD.WaitForIdle();
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            vkTarget.GetImageLayout(0, 0));

        CommandList secondReadback = RF.CreateCommandList();
        secondReadback.Begin();
        secondReadback.CopyTexture(target, capture);
        secondReadback.End();
        GD.SubmitCommands(secondReadback);
        GD.WaitForIdle();
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            vkTarget.GetImageLayout(0, 0));
        AssertPixels(capture, RgbaByte.Red);
        Assert.Empty(GD.CheckValidation(
            "render-target readback layout restoration"));
    }

    [SkippableFact]
    public void ResolveRoundTripRestoresSourceAndDestinationAttachmentLayouts()
    {
        TextureSampleCount sampleCount =
            GD.GetSampleCountLimit(ColorFormat, depthFormat: false);
        Skip.If(
            sampleCount == TextureSampleCount.Count1,
            "NV-SKIP-VULKAN-MSAA-RESOLVE: Multisample color attachments are unavailable on this Vulkan device.");

        Texture source = RF.CreateTexture(
            TextureDescription.Texture2D(
                Size,
                Size,
                1,
                1,
                ColorFormat,
                TextureUsage.RenderTarget,
                sampleCount));
        Texture destination = CreateRenderTarget();
        VkTexture vkSource = Assert.IsType<VkTexture>(source);
        VkTexture vkDestination = Assert.IsType<VkTexture>(destination);
        Framebuffer sourceFramebuffer = RF.CreateFramebuffer(
            new FramebufferDescription(null, source));
        Framebuffer destinationFramebuffer = RF.CreateFramebuffer(
            new FramebufferDescription(null, destination));
        Texture capture = CreateCapture();

        CommandList initialize = RF.CreateCommandList();
        initialize.Begin();
        initialize.SetFramebuffer(sourceFramebuffer);
        initialize.ClearColorTarget(0, RgbaFloat.Red);
        initialize.SetFramebuffer(destinationFramebuffer);
        initialize.ClearColorTarget(0, RgbaFloat.Blue);
        initialize.End();
        GD.SubmitCommands(initialize);
        GD.WaitForIdle();
        AssertAttachmentLayouts(vkSource, vkDestination);

        CommandList firstResolve = RF.CreateCommandList();
        firstResolve.Begin();
        firstResolve.ResolveTexture(source, destination);
        AssertAttachmentLayouts(vkSource, vkDestination);
        firstResolve.CopyTexture(destination, capture);
        AssertAttachmentLayouts(vkSource, vkDestination);
        firstResolve.End();
        GD.SubmitCommands(firstResolve);
        GD.WaitForIdle();
        AssertAttachmentLayouts(vkSource, vkDestination);
        AssertPixels(capture, RgbaByte.Red);

        // Give the two attachments independently observable contents while
        // exercising each one through a later render-pass load/clear cycle.
        CommandList reuse = RF.CreateCommandList();
        reuse.Begin();
        reuse.SetFramebuffer(sourceFramebuffer);
        reuse.ClearColorTarget(0, RgbaFloat.Green);
        reuse.SetFramebuffer(destinationFramebuffer);
        reuse.End();
        GD.SubmitCommands(reuse);
        GD.WaitForIdle();
        AssertAttachmentLayouts(vkSource, vkDestination);

        CommandList preservedDestinationReadback = RF.CreateCommandList();
        preservedDestinationReadback.Begin();
        preservedDestinationReadback.CopyTexture(destination, capture);
        preservedDestinationReadback.End();
        GD.SubmitCommands(preservedDestinationReadback);
        GD.WaitForIdle();
        AssertPixels(capture, RgbaByte.Red);

        CommandList secondResolve = RF.CreateCommandList();
        secondResolve.Begin();
        secondResolve.ResolveTexture(source, destination);
        AssertAttachmentLayouts(vkSource, vkDestination);
        secondResolve.CopyTexture(destination, capture);
        secondResolve.End();
        GD.SubmitCommands(secondResolve);
        GD.WaitForIdle();
        AssertAttachmentLayouts(vkSource, vkDestination);
        AssertPixels(capture, RgbaByte.Green);
        Assert.Empty(GD.CheckValidation(
            "render-target resolve and reuse layout restoration"));
    }

    [Fact]
    public void SuspendedSampledFramebufferFinalizesAtCommandListEnd()
    {
        Texture target = RF.CreateTexture(
            TextureDescription.Texture2D(
                Size,
                Size,
                1,
                1,
                ColorFormat,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
        VkTexture vkTarget = Assert.IsType<VkTexture>(target);
        Framebuffer framebuffer = RF.CreateFramebuffer(
            new FramebufferDescription(null, target));
        Texture capture = CreateCapture();
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.SetFramebuffer(framebuffer);
        commandList.ClearColorTarget(0, RgbaFloat.Red);
        commandList.CopyTexture(target, capture);
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            vkTarget.GetImageLayout(0, 0));
        commandList.End();
        Assert.Equal(
            ImageLayout.ShaderReadOnlyOptimal,
            vkTarget.GetImageLayout(0, 0));
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.Equal(
            ImageLayout.ShaderReadOnlyOptimal,
            vkTarget.GetImageLayout(0, 0));
        AssertPixels(capture, RgbaByte.Red);
        Assert.Empty(GD.CheckValidation(
            "terminally suspended framebuffer finalization"));
    }

    [Fact]
    public void DepthStencilResolveIsRejectedBeforeNativeRecording()
    {
        const PixelFormat depthFormat = PixelFormat.R16_UNorm;
        Texture source = RF.CreateTexture(
            TextureDescription.Texture2D(
                Size,
                Size,
                1,
                1,
                depthFormat,
                TextureUsage.DepthStencil));
        Texture destination = RF.CreateTexture(
            TextureDescription.Texture2D(
                Size,
                Size,
                1,
                1,
                depthFormat,
                TextureUsage.DepthStencil));
        VkTexture vkSource = Assert.IsType<VkTexture>(source);
        VkTexture vkDestination = Assert.IsType<VkTexture>(destination);
        ImageLayout sourceLayoutBefore = vkSource.GetImageLayout(0, 0);
        ImageLayout destinationLayoutBefore =
            vkDestination.GetImageLayout(0, 0);
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        NeoVeldridException error = Assert.Throws<NeoVeldridException>(
            () => commandList.ResolveTexture(source, destination));
        Assert.Contains("supports color textures only", error.Message);
        Assert.Equal(sourceLayoutBefore, vkSource.GetImageLayout(0, 0));
        Assert.Equal(
            destinationLayoutBefore,
            vkDestination.GetImageLayout(0, 0));
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();
        Assert.Empty(GD.CheckValidation(
            "depth-stencil resolve contract rejection"));
    }

    [Fact]
    public void DepthSampleCountLimitUsesDepthFormatMapping()
    {
        VkSampleCountQuery color = VkSampleCountQuery.Create(
            PixelFormat.R16_UNorm,
            depthFormat: false);
        Assert.Equal(Format.R16Unorm, color.Format);
        Assert.Equal(
            ImageUsageFlags.SampledBit |
            ImageUsageFlags.ColorAttachmentBit,
            color.Usage);

        VkSampleCountQuery depth = VkSampleCountQuery.Create(
            PixelFormat.R16_UNorm,
            depthFormat: true);
        Assert.Equal(Format.D16Unorm, depth.Format);
        Assert.Equal(
            ImageUsageFlags.SampledBit |
            ImageUsageFlags.DepthStencilAttachmentBit,
            depth.Usage);
    }

    private Texture CreateRenderTarget()
        => RF.CreateTexture(
            TextureDescription.Texture2D(
                Size,
                Size,
                1,
                1,
                ColorFormat,
                TextureUsage.RenderTarget));

    private Texture CreateCapture()
        => RF.CreateTexture(
            TextureDescription.Texture2D(
                Size,
                Size,
                1,
                1,
                ColorFormat,
                TextureUsage.Staging));

    private static void AssertAttachmentLayouts(
        VkTexture source,
        VkTexture destination)
    {
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            source.GetImageLayout(0, 0));
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            destination.GetImageLayout(0, 0));
    }

    private void AssertPixels(Texture capture, RgbaByte expected)
    {
        MappedResourceView<RgbaByte> mapped =
            GD.Map<RgbaByte>(capture, MapMode.Read);
        try
        {
            for (uint y = 0; y < Size; y++)
            {
                for (uint x = 0; x < Size; x++)
                    Assert.Equal(expected, mapped[x, y]);
            }
        }
        finally
        {
            GD.Unmap(capture);
        }
    }
}
#endif
