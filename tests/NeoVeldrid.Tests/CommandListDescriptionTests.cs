using Xunit;

namespace NeoVeldrid.Tests;

public class CommandListDescriptionTests
{
    [Fact]
    public void CapacityValuesParticipateInEqualityAndHashing()
    {
        CommandListDescription description = new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 8,
            InitialTrackedResourceCapacityPerSubmission = 256
        };
        CommandListDescription same = new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 8,
            InitialTrackedResourceCapacityPerSubmission = 256
        };

        Assert.True(description.Equals(same));
        Assert.Equal(description.GetHashCode(), same.GetHashCode());

        same.MaximumInFlightSubmissionCount = 7;
        Assert.False(description.Equals(same));

        same.MaximumInFlightSubmissionCount = 8;
        same.InitialTrackedResourceCapacityPerSubmission = 255;
        Assert.False(description.Equals(same));
    }

    [Fact]
    public void DefaultDescriptionUsesAdaptiveZeroValues()
    {
        CommandListDescription description = default;

        Assert.Equal(0u, description.MaximumInFlightSubmissionCount);
        Assert.Equal(0u, description.InitialTrackedResourceCapacityPerSubmission);
        Assert.True(description.Equals(default));
    }
}
