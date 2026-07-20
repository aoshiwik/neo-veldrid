namespace NeoVeldrid;

/// <summary>
/// Observes the backend ownership which keeps an ordered texture-upload
/// destination alive from submission admission through the backend's release
/// boundary. This seam is internal and opt-in so normal command recording does
/// not allocate or retain additional resources.
/// </summary>
internal interface ICommandListTextureUploadLifecycleObserver
{
    /// <summary>
    /// Called after the backend's real submission-retention mechanism owns the
    /// destination. The callback may block in deterministic lifecycle tests.
    /// </summary>
    void OnRetentionAcquired(CommandList commandList, Texture destination);

    /// <summary>
    /// Called exactly once after that backend-owned retention is released.
    /// This callback deliberately carries no resource object: observing the
    /// release must not create a replacement managed owner for the resource
    /// whose lifetime is being measured.
    /// </summary>
    /// <param name="commandList">The command list whose retention ended.</param>
    /// <param name="remainingBackendOwnershipCount">
    /// The observable backend ownership count remaining for the destination
    /// after the submission retention is released. Backends without a native
    /// per-resource counter report the count retained by their real ownership
    /// container.
    /// </param>
    void OnRetentionReleased(
        CommandList commandList,
        int remainingBackendOwnershipCount);
}
