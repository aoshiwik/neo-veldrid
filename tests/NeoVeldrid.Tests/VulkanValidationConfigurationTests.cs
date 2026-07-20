#if TEST_VULKAN
using NeoVeldrid.Vk;
using Silk.NET.Vulkan;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanValidationConfigurationTests
{
    [Fact]
    public void DefaultOptionsUseAutomaticValidation()
    {
        Assert.Equal(
            VulkanValidationMode.Automatic,
            default(VulkanDeviceOptions).ValidationMode);
    }

    [Fact]
    public void RequiredModeRejectsNonDebugDevice()
    {
        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(() =>
            VkValidationConfiguration.Resolve(
                false,
                VulkanValidationMode.Required,
                CompleteCapabilities()));

        Assert.Contains(nameof(GraphicsDeviceOptions.Debug), exception.Message);
    }

    [Fact]
    public void RequiredModeRejectsMissingKhronosLayer()
    {
        VkValidationCapabilities capabilities = CompleteCapabilities() with
        {
            HasKhronosValidationLayer = false,
            HasStandardValidationLayer = true,
        };

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(() =>
            VkValidationConfiguration.Resolve(
                true,
                VulkanValidationMode.Required,
                capabilities));

        Assert.Contains("VK_LAYER_KHRONOS_validation", exception.Message);
    }

    [Fact]
    public void RequiredSynchronizationRejectsMissingValidationFeaturesExtension()
    {
        VkValidationCapabilities capabilities = CompleteCapabilities() with
        {
            ValidationFeaturesSpecVersion = 0,
        };

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(() =>
            VkValidationConfiguration.Resolve(
                true,
                VulkanValidationMode.RequiredSynchronization,
                capabilities));

        Assert.Contains("VK_EXT_validation_features", exception.Message);
    }

    [Fact]
    public void RequiredSynchronizationProducesExactActivationContract()
    {
        VkValidationConfiguration configuration =
            VkValidationConfiguration.Resolve(
                true,
                VulkanValidationMode.RequiredSynchronization,
                CompleteCapabilities());

        Assert.True(configuration.Requested);
        Assert.True(configuration.Required);
        Assert.True(configuration.EnableDebugUtils);
        Assert.True(configuration.EnableSynchronizationValidation);
        Assert.Equal(VkValidationLayer.Khronos, configuration.Layer);
        Assert.Equal(2u, configuration.ValidationFeaturesSpecVersion);
        Assert.Empty(configuration.InactiveReason);
    }

    [Fact]
    public void RequiredSynchronizationDoesNotInferActivationFromAdvertisedRevision()
    {
        VkValidationConfiguration configuration =
            VkValidationConfiguration.Resolve(
                true,
                VulkanValidationMode.RequiredSynchronization,
                CompleteCapabilities() with { ValidationFeaturesSpecVersion = 1 });

        Assert.True(configuration.EnableSynchronizationValidation);
        Assert.Equal(1u, configuration.ValidationFeaturesSpecVersion);
    }

    [Theory]
    [InlineData("WARNING-CreateInstance-status-message")]
    [InlineData("UNASSIGNED-CreateInstance-status-message")]
    [InlineData("UNASSIGNED-khronos-validation-createinstance-status-message")]
    public void KhronosStatusMessageProvesSynchronizationActivation(string messageId)
    {
        GraphicsDeviceValidationMessage message = StatusMessage(
            messageId,
            "VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION");

        Assert.True(
            VkValidationConfiguration.IsSynchronizationValidationActivationEvidence(message));
    }

    [Fact]
    public void CurrentKhronosStatusMessageProvesSynchronizationActivation()
    {
        GraphicsDeviceValidationMessage message = new GraphicsDeviceValidationMessage(
            1,
            GraphicsBackend.Vulkan,
            GraphicsDeviceValidationSeverity.Information,
            "VK_EXT_debug_utils",
            "GeneralBitExt",
            "CURRENT-VALIDATION-ENABLED",
            "Current Validaiton Enabled:\n"
                + "  - Core Checks\n"
                + "  - Synchronization\n"
                + "  - Stateless Parameter\n");

        Assert.True(
            VkValidationConfiguration.IsSynchronizationValidationActivationEvidence(message));
    }

    [Fact]
    public void DisabledOrUnrelatedFeatureTextIsNotActivationEvidence()
    {
        GraphicsDeviceValidationMessage disabled = new GraphicsDeviceValidationMessage(
            1,
            GraphicsBackend.Vulkan,
            GraphicsDeviceValidationSeverity.Information,
            "VK_EXT_debug_utils",
            "GeneralBitExt",
            "WARNING-CreateInstance-status-message",
            "Khronos Validation Layer Active:\n"
                + "    Current Enables: None.\n"
                + "    Current Disables: VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION.\n");
        GraphicsDeviceValidationMessage unrelated = StatusMessage(
            "NEOVELDRID-VULKAN-VALIDATION-ACTIVATION",
            "VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION");
        GraphicsDeviceValidationMessage prefixedFeature = StatusMessage(
            "WARNING-CreateInstance-status-message",
            "VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_FAKE");
        GraphicsDeviceValidationMessage currentWithoutSynchronization =
            new GraphicsDeviceValidationMessage(
                1,
                GraphicsBackend.Vulkan,
                GraphicsDeviceValidationSeverity.Information,
                "VK_EXT_debug_utils",
                "GeneralBitExt",
                "CURRENT-VALIDATION-ENABLED",
                "Current Validaiton Enabled:\r\n"
                    + "  - Core Checks\r\n"
                    + "  - Stateless Parameter\r\n");

        Assert.False(
            VkValidationConfiguration.IsSynchronizationValidationActivationEvidence(disabled));
        Assert.False(
            VkValidationConfiguration.IsSynchronizationValidationActivationEvidence(unrelated));
        Assert.False(
            VkValidationConfiguration.IsSynchronizationValidationActivationEvidence(
                prefixedFeature));
        Assert.False(
            VkValidationConfiguration.IsSynchronizationValidationActivationEvidence(
                currentWithoutSynchronization));
    }

    [Fact]
    public void AutomaticModeDegradesWithoutClaimingActivation()
    {
        VkValidationConfiguration configuration =
            VkValidationConfiguration.Resolve(
                true,
                VulkanValidationMode.Automatic,
                CompleteCapabilities() with { HasDebugUtils = false });

        Assert.True(configuration.Requested);
        Assert.False(configuration.Required);
        Assert.False(configuration.EnableDebugUtils);
        Assert.False(configuration.EnableSynchronizationValidation);
        Assert.Equal(VkValidationLayer.None, configuration.Layer);
        Assert.Contains("unavailable", configuration.InactiveReason);
    }

    [Theory]
    [InlineData(Result.Success, true)]
    [InlineData(Result.Timeout, false)]
    public void FenceWaitResultsPreserveCompletionAndTimeout(
        Result result,
        bool expected)
    {
        Assert.Equal(expected, VkGraphicsDevice.GetFenceWaitResult(result));
    }

    [Fact]
    public void FenceWaitResultsDoNotHideDeviceFailure()
    {
        Assert.Throws<NeoVeldridException>(() =>
            VkGraphicsDevice.GetFenceWaitResult(Result.ErrorDeviceLost));
    }

    private static VkValidationCapabilities CompleteCapabilities() =>
        new VkValidationCapabilities(
            HasDebugUtils: true,
            HasStandardValidationLayer: true,
            HasKhronosValidationLayer: true,
            // Khronos' own current validation-layer manifest advertises
            // revision 2 while implementing later validation-feature enums.
            ValidationFeaturesSpecVersion: 2);

    private static GraphicsDeviceValidationMessage StatusMessage(
        string messageId,
        string enabledFeatures) =>
        new GraphicsDeviceValidationMessage(
            1,
            GraphicsBackend.Vulkan,
            GraphicsDeviceValidationSeverity.Information,
            "VK_EXT_debug_utils",
            "GeneralBitExt",
            messageId,
            "Khronos Validation Layer Active:\n"
                + $"    Current Enables: {enabledFeatures}.\n"
                + "    Current Disables: None.\n");
}
#endif
