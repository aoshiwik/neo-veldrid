#if TEST_VULKAN
using NeoVeldrid.Vk;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanBufferSubmissionAccessTests
{
    [Fact]
    public void HostWriteIsAllowedWithoutSubmissionUse()
    {
        var access = new VkMappableResourceSubmissionAccess();

        access.BeginHostWrite("buffer");

        Assert.True(access.HostWriteActive);
        Assert.Equal(0, access.SubmissionUseCount);

        access.EndHostWrite();
        Assert.False(access.HostWriteActive);
    }

    [Fact]
    public void HostWriteIsRejectedUntilEverySubmissionUseCompletes()
    {
        var access = new VkMappableResourceSubmissionAccess();
        access.BeginSubmissionUse();
        access.BeginSubmissionUse();

        NeoVeldridException first = Assert.Throws<NeoVeldridException>(
            () => access.BeginHostWrite("shared"));
        Assert.Contains("2 submitted GPU use(s)", first.Message);

        access.EndSubmissionUse();
        Assert.Throws<NeoVeldridException>(
            () => access.BeginHostWrite("shared"));

        access.EndSubmissionUse();
        access.BeginHostWrite("shared");
        access.EndHostWrite();
    }

    [Fact]
    public void SubmissionUseCannotBeginDuringHostWrite()
    {
        var access = new VkMappableResourceSubmissionAccess();
        access.BeginHostWrite("shared");

        Assert.Throws<NeoVeldridException>(access.BeginSubmissionUse);
        Assert.Equal(0, access.SubmissionUseCount);

        access.EndHostWrite();
        access.BeginSubmissionUse();
        access.EndSubmissionUse();
    }

    [Fact]
    public void RepeatedHostMapsShareOneExclusiveHostAccessPeriod()
    {
        var access = new VkMappableResourceSubmissionAccess();

        access.BeginHostMap("shared");
        access.BeginHostMap("shared");
        access.BeginHostMap("shared");

        Assert.Equal(3, access.HostMapCount);
        Assert.Throws<NeoVeldridException>(access.BeginSubmissionUse);
        Assert.Throws<NeoVeldridException>(() => access.BeginHostWrite("shared"));

        access.EndHostMap();
        access.EndHostMap();
        access.EndHostMap();
        Assert.Equal(0, access.HostMapCount);

        access.BeginSubmissionUse();
        access.EndSubmissionUse();
    }

    [Fact]
    public void HostMapIsRejectedUntilEverySubmissionUseCompletes()
    {
        var access = new VkMappableResourceSubmissionAccess();
        access.BeginSubmissionUse();
        access.BeginSubmissionUse();

        NeoVeldridException error = Assert.Throws<NeoVeldridException>(
            () => access.BeginHostMap("shared"));
        Assert.Contains("2 submitted GPU use(s)", error.Message);

        access.EndSubmissionUse();
        access.EndSubmissionUse();
        access.BeginHostMap("shared");
        access.EndHostMap();
    }

    [Fact]
    public void UnbalancedReleasesAreRejected()
    {
        var access = new VkMappableResourceSubmissionAccess();

        Assert.Throws<NeoVeldridException>(access.EndSubmissionUse);
        Assert.Throws<NeoVeldridException>(access.EndHostWrite);
        Assert.Throws<NeoVeldridException>(access.EndHostMap);
    }
}
#endif
