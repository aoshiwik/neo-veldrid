using System;

namespace NeoVeldrid;

/// <summary>
/// Describes a <see cref="CommandList"/>, for creation using a <see cref="ResourceFactory"/>.
/// </summary>
public struct CommandListDescription : IEquatable<CommandListDescription>
{
    /// <summary>
    /// Gets or sets the maximum number of submissions from this command list
    /// that a backend may retain concurrently. Zero selects adaptive backend
    /// behavior without an explicit bound.
    /// </summary>
    public uint MaximumInFlightSubmissionCount { readonly get; set; }

    /// <summary>
    /// Gets or sets the initial number of distinct resources retained for each
    /// in-flight submission. Zero selects lazy backend allocation.
    /// </summary>
    public uint InitialTrackedResourceCapacityPerSubmission { readonly get; set; }

    /// <summary>
    /// Element-wise equality.
    /// </summary>
    /// <param name="other">The instance to compare to.</param>
    /// <returns>True if all elements are equal; false otherwise.</returns>
    public readonly bool Equals(CommandListDescription other)
    {
        return MaximumInFlightSubmissionCount == other.MaximumInFlightSubmissionCount &&
               InitialTrackedResourceCapacityPerSubmission ==
               other.InitialTrackedResourceCapacityPerSubmission;
    }

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    /// <returns>A 32-bit signed integer that is the hash code for this instance.</returns>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(
            MaximumInFlightSubmissionCount,
            InitialTrackedResourceCapacityPerSubmission);
    }
}
