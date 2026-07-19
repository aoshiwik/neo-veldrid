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
            InitialTrackedResourceCapacityPerSubmission = 256,
            InitialStagingUploadPageSize = 1_048_576
        };
        CommandListDescription same = new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 8,
            InitialTrackedResourceCapacityPerSubmission = 256,
            InitialStagingUploadPageSize = 1_048_576
        };

        Assert.True(description.Equals(same));
        Assert.Equal(description.GetHashCode(), same.GetHashCode());

        same.MaximumInFlightSubmissionCount = 7;
        Assert.False(description.Equals(same));

        same.MaximumInFlightSubmissionCount = 8;
        same.InitialTrackedResourceCapacityPerSubmission = 255;
        Assert.False(description.Equals(same));

        same.InitialTrackedResourceCapacityPerSubmission = 256;
        same.InitialStagingUploadPageSize = 524_288;
        Assert.False(description.Equals(same));
    }

    [Fact]
    public void DefaultDescriptionUsesAdaptiveZeroValues()
    {
        CommandListDescription description = default;

        Assert.Equal(0u, description.MaximumInFlightSubmissionCount);
        Assert.Equal(0u, description.InitialTrackedResourceCapacityPerSubmission);
        Assert.Equal(0u, description.InitialStagingUploadPageSize);
        Assert.True(description.Equals(default));
    }
}
