#if TEST_VULKAN
using NeoVeldrid.Vk;
using Silk.NET.Vulkan;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanSwapchainPresentationStateTests
{
    [Theory]
    [InlineData(Result.Success, 0)]
    [InlineData(Result.SuboptimalKhr, 1)]
    [InlineData(Result.ErrorOutOfDateKhr, 2)]
    public void AcquireResultsPreserveVulkanSuccessSemantics(
        Result result,
        int expectedDisposition)
    {
        Assert.Equal(
            (VkSwapchainAcquireDisposition)expectedDisposition,
            VkSwapchainPresentationState.ClassifyAcquireResult(result));
    }

    [Fact]
    public void SuboptimalAcquireRemainsPresentableThenRequestsReplacement()
    {
        VkSwapchainPresentationState state = new VkSwapchainPresentationState();

        state.CommitAcquiredImage(
            imageIndex: 2,
            imageCount: 3,
            recreateAfterPresent: true);

        Assert.True(state.HasAcquiredImage);
        Assert.True(state.RecreateAfterPresent);
        Assert.Equal(2u, state.RequireAcquiredImageIndex(imageCount: 3));
        Assert.True(state.CompletePresentation(Result.Success));
        Assert.False(state.HasAcquiredImage);
    }

    [Theory]
    [InlineData(Result.SuboptimalKhr)]
    [InlineData(Result.ErrorOutOfDateKhr)]
    public void PresentResultRequestsReplacementAfterReleasingAcquiredImage(
        Result presentResult)
    {
        VkSwapchainPresentationState state = new VkSwapchainPresentationState();
        state.CommitAcquiredImage(
            imageIndex: 1,
            imageCount: 3,
            recreateAfterPresent: false);

        Assert.True(state.CompletePresentation(presentResult));
        Assert.False(state.HasAcquiredImage);
        Assert.False(state.RecreateAfterPresent);
    }

    [Fact]
    public void ReacquiringAnImageRequiresItsPreviousPresentationToComplete()
    {
        VkSwapchainPresentationState state = new VkSwapchainPresentationState();
        state.CommitAcquiredImage(
            imageIndex: 1,
            imageCount: 3,
            recreateAfterPresent: false);

        Assert.Throws<NeoVeldridException>(() => state.CommitAcquiredImage(
            imageIndex: 1,
            imageCount: 3,
            recreateAfterPresent: false));

        Assert.False(state.CompletePresentation(Result.Success));
        state.CommitAcquiredImage(
            imageIndex: 1,
            imageCount: 3,
            recreateAfterPresent: false);
        Assert.Equal(1u, state.RequireAcquiredImageIndex(imageCount: 3));
    }

    [Fact]
    public void ReplacementInvalidatesThePreviouslyAcquiredImage()
    {
        VkSwapchainPresentationState state = new VkSwapchainPresentationState();
        state.CommitAcquiredImage(
            imageIndex: 0,
            imageCount: 2,
            recreateAfterPresent: false);

        state.ReplaceSwapchain();

        Assert.False(state.HasAcquiredImage);
        Assert.Throws<NeoVeldridException>(() =>
            state.RequireAcquiredImageIndex(imageCount: 2));
    }

    [Fact]
    public void AcquiredImageSelectsTheSamePerImageSynchronizationSlot()
    {
        VkSwapchainPresentationState state = new VkSwapchainPresentationState();
        state.CommitAcquiredImage(
            imageIndex: 2,
            imageCount: 4,
            recreateAfterPresent: false);

        uint synchronizationSlot = state.RequireAcquiredImageIndex(imageCount: 4);

        Assert.Equal(2u, synchronizationSlot);
    }
}
#endif
