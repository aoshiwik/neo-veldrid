using System;

namespace NeoVeldrid;

/// <summary>
/// Describes a <see cref="CommandList"/>, for creation using a <see cref="ResourceFactory"/>.
/// </summary>
public struct CommandListDescription : IEquatable<CommandListDescription>
{
    /// <summary>
    /// Gets or sets the maximum number of submissions from this command list
    /// that a backend may retain concurrently. Zero selects backend policy
    /// (adaptive Vulkan submissions; three D3D11 staging generations), without
    /// a caller-specified bound.
    /// </summary>
    public uint MaximumInFlightSubmissionCount { readonly get; set; }

    /// <summary>
    /// Gets or sets the initial number of distinct resources retained for each
    /// in-flight submission. Zero selects lazy backend allocation.
    /// </summary>
    public uint InitialTrackedResourceCapacityPerSubmission { readonly get; set; }

    /// <summary>
    /// Gets or sets the capacity, in bytes, of each submission slot's retained
    /// staging upload page on backends which use submission-owned staging pages
    /// (currently Vulkan). Zero keeps lazy backend allocation. Other backends
    /// may ignore this hint. A non-zero value moves the expected Vulkan staging
    /// allocation to command-list creation and keeps interactive uploads within
    /// a fixed native-memory envelope.
    /// </summary>
    public uint InitialStagingUploadPageSize { readonly get; set; }

    /// <summary>
    /// Element-wise equality.
    /// </summary>
    /// <param name="other">The instance to compare to.</param>
    /// <returns>True if all elements are equal; false otherwise.</returns>
    public readonly bool Equals(CommandListDescription other)
    {
        return MaximumInFlightSubmissionCount == other.MaximumInFlightSubmissionCount &&
               InitialTrackedResourceCapacityPerSubmission ==
               other.InitialTrackedResourceCapacityPerSubmission &&
               InitialStagingUploadPageSize ==
               other.InitialStagingUploadPageSize;
    }

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    /// <returns>A 32-bit signed integer that is the hash code for this instance.</returns>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(
            MaximumInFlightSubmissionCount,
            InitialTrackedResourceCapacityPerSubmission,
            InitialStagingUploadPageSize);
    }
}
