#if TEST_VULKAN
using System;
using System.Linq;
using NeoVeldrid.Vk;
using Silk.NET.Vulkan;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanSynchronizationValidationOracleTests
{
    private const string ExpectedHazardId =
        "SYNC-HAZARD-WRITE-AFTER-WRITE";

    [SkippableFact]
    public void NativeBufferHazardProvesSynchronizationValidation()
    {
        SkipUnlessSynchronizationValidationIsAvailable();

        VulkanDeviceOptions options = new VulkanDeviceOptions(
            instanceExtensions: null,
            deviceExtensions: null,
            validationMode: VulkanValidationMode.RequiredSynchronization);
        using GraphicsDevice device = GraphicsDevice.CreateVulkan(
            new GraphicsDeviceOptions(debug: true),
            options);

        VkGraphicsDevice vkDevice = Assert.IsType<VkGraphicsDevice>(device);
        Assert.True(
            device.Validation.Status.HasFeature(
                GraphicsDeviceValidationFeatures.SynchronizationValidation),
            device.Validation.Status.ToString());
        Assert.Empty(device.CheckValidation("synchronization oracle precondition"));

        const uint bufferSize = 256;
        using DeviceBuffer source = device.ResourceFactory.CreateBuffer(
            new BufferDescription(bufferSize, BufferUsage.VertexBuffer));
        using DeviceBuffer destination = device.ResourceFactory.CreateBuffer(
            new BufferDescription(bufferSize, BufferUsage.VertexBuffer));
        using CommandList commandList = device.ResourceFactory.CreateCommandList();

        VkBuffer sourceVk = Assert.IsType<VkBuffer>(source);
        VkBuffer destinationVk = Assert.IsType<VkBuffer>(destination);
        VkCommandList commandListVk = Assert.IsType<VkCommandList>(commandList);

        // Bypass NeoVeldrid's synchronizing copy recorder deliberately. These
        // native commands are never submitted; their only purpose is to prove
        // that the active layer recognizes an otherwise valid missing barrier.
        commandList.Begin();
        BufferCopy copy = new BufferCopy
        {
            Size = bufferSize
        };
        vkDevice.Vk.CmdCopyBuffer(
            commandListVk.CommandBuffer,
            sourceVk.DeviceBuffer,
            destinationVk.DeviceBuffer,
            1,
            in copy);
        vkDevice.Vk.CmdFillBuffer(
            commandListVk.CommandBuffer,
            destinationVk.DeviceBuffer,
            0,
            bufferSize,
            0xA5A5A5A5u);

        GraphicsDeviceValidationException exception =
            Assert.Throws<GraphicsDeviceValidationException>(commandList.End);
        Assert.Equal("command-list recording", exception.Boundary);

        GraphicsDeviceValidationMessage hazard = Assert.Single(exception.Messages);
        Assert.Equal(ExpectedHazardId, hazard.Id);
        Assert.Equal(GraphicsDeviceValidationSeverity.Error, hazard.Severity);
        Assert.Equal("VK_EXT_debug_utils", hazard.Source);

        Assert.Empty(device.CheckValidation("synchronization oracle replay"));
    }

    private static void SkipUnlessSynchronizationValidationIsAvailable()
    {
        string khronosLayer = CommonStrings.KhronosValidationLayerName.ToString();
        Skip.IfNot(
            VulkanUtil.EnumerateInstanceLayers().Contains(khronosLayer),
            "NV-SKIP-VULKAN-VALIDATION-LAYER: VK_LAYER_KHRONOS_validation is unavailable on this host.");
        Skip.IfNot(
            VulkanUtil.GetInstanceExtensionSpecVersion(
                CommonStrings.VK_EXT_VALIDATION_FEATURES_EXTENSION_NAME.ToString(),
                khronosLayer)
                != 0,
            "NV-SKIP-VULKAN-SYNC-VALIDATION: VK_EXT_validation_features is unavailable from VK_LAYER_KHRONOS_validation on this host.");
    }
}
#endif
