#if TEST_VULKAN
using System;
using NeoVeldrid.Vk;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanResourceRefCountTests
{
    [Fact]
    public void FailedFinalReleaseCanBeRetried()
    {
        int attempts = 0;
        ResourceRefCount reference = new ResourceRefCount(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("injected cleanup failure");
            }
        });

        Assert.Throws<InvalidOperationException>(() => reference.Decrement());
        Assert.Equal(0, reference.Decrement());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void NullReleaseActionIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ResourceRefCount(null));
    }
}
#endif
