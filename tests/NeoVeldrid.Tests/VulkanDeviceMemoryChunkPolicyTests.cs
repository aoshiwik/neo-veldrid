#if TEST_VULKAN
using System;
using NeoVeldrid.Vk;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanDeviceMemoryChunkPolicyTests
{
    private const ulong Mebibyte = 1024 * 1024;

    [Fact]
    public void SmallAllocationsUseDemandAppropriateBaselines()
    {
        Assert.Equal(
            16 * Mebibyte,
            VkDeviceMemoryChunkPolicy.GetChunkSize(
                persistentMapped: true,
                allocationSize: 1,
                alignment: 1));
        Assert.Equal(
            64 * Mebibyte,
            VkDeviceMemoryChunkPolicy.GetChunkSize(
                persistentMapped: false,
                allocationSize: 1,
                alignment: 1));
    }

    [Theory]
    [InlineData(true, 17, 32)]
    [InlineData(true, 33, 64)]
    [InlineData(false, 65, 128)]
    [InlineData(false, 129, 256)]
    public void ChunkSizeGrowsByPowersOfTwoToFitDemand(
        bool persistentMapped,
        ulong allocationMebibytes,
        ulong expectedChunkMebibytes)
    {
        Assert.Equal(
            expectedChunkMebibytes * Mebibyte,
            VkDeviceMemoryChunkPolicy.GetChunkSize(
                persistentMapped,
                allocationMebibytes * Mebibyte,
                alignment: 1));
    }

    [Fact]
    public void AlignmentParticipatesInChunkDemand()
    {
        Assert.Equal(
            32 * Mebibyte,
            VkDeviceMemoryChunkPolicy.GetChunkSize(
                persistentMapped: true,
                allocationSize: 1 * Mebibyte,
                alignment: 32 * Mebibyte));
    }

    [Theory]
    [InlineData(true, 64)]
    [InlineData(false, 256)]
    public void DedicatedThresholdIsAlsoThePooledChunkCeiling(
        bool persistentMapped,
        ulong thresholdMebibytes)
    {
        ulong threshold = thresholdMebibytes * Mebibyte;

        Assert.Equal(
            threshold,
            VkDeviceMemoryChunkPolicy.GetDedicatedAllocationThreshold(
                persistentMapped));
        Assert.Equal(
            threshold,
            VkDeviceMemoryChunkPolicy.GetChunkSize(
                persistentMapped,
                allocationSize: threshold - 1,
                alignment: threshold * 2));
    }

    [Fact]
    public void ZeroAlignmentIsRejectedWithoutADeviceAllocation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VkDeviceMemoryChunkPolicy.GetChunkSize(
                persistentMapped: true,
                allocationSize: 1,
                alignment: 0));
    }
}
#endif
