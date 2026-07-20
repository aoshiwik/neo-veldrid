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
    public void RequiredSynchronizationRejectsOldValidationFeaturesRevision()
    {
        VkValidationCapabilities capabilities = CompleteCapabilities() with
        {
            ValidationFeaturesSpecVersion =
                VkValidationConfiguration.MinimumSynchronizationValidationFeaturesSpecVersion - 1,
        };

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(() =>
            VkValidationConfiguration.Resolve(
                true,
                VulkanValidationMode.RequiredSynchronization,
                capabilities));

        Assert.Contains("revision 4", exception.Message);
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
        Assert.Equal(
            VkValidationConfiguration.MinimumSynchronizationValidationFeaturesSpecVersion,
            configuration.ValidationFeaturesSpecVersion);
        Assert.Empty(configuration.InactiveReason);
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
            ValidationFeaturesSpecVersion:
                VkValidationConfiguration.MinimumSynchronizationValidationFeaturesSpecVersion);
}
#endif
