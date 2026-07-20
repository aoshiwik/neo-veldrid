#if TEST_VULKAN
using System;
using System.Linq;
using NeoVeldrid.Vk;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed unsafe class VulkanValidationDebugMessengerTests
{
    [SkippableFact]
    public void NativeMessagesAreCompleteFatalAndInstanceScoped()
    {
        using GraphicsDevice first = TestUtils.CreateVulkanDevice();
        using GraphicsDevice second = TestUtils.CreateVulkanDevice();
        Skip.IfNot(
            first.IsDebugActive && second.IsDebugActive,
            "NV-SKIP-VULKAN-DEBUG-UTILS: VK_EXT_debug_utils is unavailable on this host.");

        VkGraphicsDevice firstVk = Assert.IsType<VkGraphicsDevice>(first);
        Assert.True(
            firstVk.Vk.TryGetInstanceExtension(
                firstVk.Instance,
                out ExtDebugUtils debugUtils));

        const string firstId = "NEOVELDRID-VULKAN-INSTANCE-ONE-A";
        const string secondId = "NEOVELDRID-VULKAN-INSTANCE-ONE-B";
        SubmitError(debugUtils, firstVk.Instance, firstId);
        SubmitError(debugUtils, firstVk.Instance, secondId);

        GraphicsDeviceValidationException exception =
            Assert.Throws<GraphicsDeviceValidationException>(() =>
                first.CheckValidation("native debug messenger test"));

        Assert.Equal(2, exception.Messages.Count);
        Assert.Collection(
            exception.Messages,
            message => Assert.Equal(firstId, message.Id),
            message => Assert.Equal(secondId, message.Id));

        Assert.Empty(first.CheckValidation("post-drain validation test"));
        Assert.Empty(second.CheckValidation("unrelated device validation test"));
        Assert.DoesNotContain(
            second.Validation.GetMessageHistory(),
            message => message.Id == firstId || message.Id == secondId);
    }

    [SkippableFact]
    public void RequiredSynchronizationValidationReportsActivationEvidence()
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

        VulkanDeviceOptions options = new VulkanDeviceOptions(
            instanceExtensions: null,
            deviceExtensions: null,
            validationMode: VulkanValidationMode.RequiredSynchronization);
        using GraphicsDevice device = GraphicsDevice.CreateVulkan(
            new GraphicsDeviceOptions(debug: true),
            options);

        BackendInfoVulkan info = device.GetVulkanInfo();
        Assert.True(device.IsDebugActive);
        Assert.True(info.IsSynchronizationValidationActive);
        Assert.Equal(khronosLayer, info.ActiveValidationLayer);
        Assert.NotEqual(0u, info.ValidationFeaturesSpecVersion);
        Assert.True(
            info.ValidationStatus.HasFeature(
                GraphicsDeviceValidationFeatures.SynchronousMessageDelivery));
    }

    [Theory]
    [InlineData(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt, GraphicsDeviceValidationSeverity.Error)]
    [InlineData(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt, GraphicsDeviceValidationSeverity.Warning)]
    [InlineData(DebugUtilsMessageSeverityFlagsEXT.InfoBitExt, GraphicsDeviceValidationSeverity.Information)]
    [InlineData(DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt, GraphicsDeviceValidationSeverity.Verbose)]
    public void NativeSeverityIsNormalized(
        DebugUtilsMessageSeverityFlagsEXT nativeSeverity,
        GraphicsDeviceValidationSeverity expectedSeverity)
    {
        Assert.Equal(
            expectedSeverity,
            VkGraphicsDevice.NormalizeValidationSeverity(nativeSeverity));
    }

    private static void SubmitError(
        ExtDebugUtils debugUtils,
        Instance instance,
        string id)
    {
        using FixedUtf8String messageId = new FixedUtf8String(id);
        using FixedUtf8String messageText = new FixedUtf8String(
            "Synthetic NeoVeldrid validation transport test message.");
        DebugUtilsMessengerCallbackDataEXT callbackData =
            new DebugUtilsMessengerCallbackDataEXT(
                sType: StructureType.DebugUtilsMessengerCallbackDataExt);
        callbackData.PMessageIdName = messageId;
        callbackData.PMessage = messageText;

        debugUtils.SubmitDebugUtilsMessage(
            instance,
            DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
            in callbackData);
    }
}
#endif
