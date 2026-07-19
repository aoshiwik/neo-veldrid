using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace NeoVeldrid.Vk;

/// <summary>
/// Owns the speculative image-layout state produced while one Vulkan command
/// buffer is recorded. Texture layouts must be projected during recording so a
/// later command list can record against the expected queue state, but those
/// projections are not authoritative until the command buffer is submitted.
/// </summary>
internal sealed class VkImageLayoutTransaction
{
    internal static object SyncRoot { get; } = new object();

    private static ulong s_nextRecordingOrder;

    private readonly Dictionary<SubresourceKey, LayoutState> _originalStates =
        new Dictionary<SubresourceKey, LayoutState>();
    private readonly List<VkTexture> _textures = new List<VkTexture>();
    private bool _committed;
    private ulong _recordingOrder;

    internal bool IsEmpty => _textures.Count == 0 && !_committed;
    internal ulong RecordingOrder
    {
        get
        {
            Debug.Assert(Monitor.IsEntered(SyncRoot));
            if (_recordingOrder == 0u)
            {
                if (s_nextRecordingOrder == ulong.MaxValue)
                {
                    throw new NeoVeldridException(
                        "The Vulkan image-layout transaction recording-order counter was exhausted.");
                }

                _recordingOrder = ++s_nextRecordingOrder;
            }

            return _recordingOrder;
        }
    }

    internal void ObserveLocked(
        VkTexture texture,
        uint subresource,
        ImageLayout layout,
        uint revision)
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));
        if (_committed)
        {
            throw new NeoVeldridException(
                "A committed Vulkan image-layout transaction cannot record additional texture use.");
        }

        _ = RecordingOrder;

        if (!_textures.Contains(texture))
        {
            texture.RefCount.Increment();
            try
            {
                texture.RegisterLayoutTransactionLocked(this);
                _textures.Add(texture);
            }
            catch
            {
                texture.RefCount.Decrement();
                throw;
            }
        }
        else
        {
            texture.ValidateRecordingTransactionLocked(this);
        }

        var key = new SubresourceKey(texture, subresource);
        _originalStates.TryAdd(key, new LayoutState(layout, revision));
    }

    /// <summary>
    /// Rejects submitting command lists in an order different from the layout
    /// projections they were recorded against.
    /// </summary>
    internal void ValidateSubmissionOrder()
    {
        lock (SyncRoot)
        {
            foreach (VkTexture texture in _textures)
                texture.ValidateSubmittingTransactionLocked(this);
        }
    }

    /// <summary>
    /// Makes the projected layouts authoritative after vkQueueSubmit accepts
    /// the command buffer.
    /// </summary>
    internal void CommitAfterSubmission()
    {
        lock (SyncRoot)
        {
            foreach (VkTexture texture in _textures)
                texture.ValidateSubmittingTransactionLocked(this);
            foreach (VkTexture texture in _textures)
                texture.CommitLayoutTransactionLocked(this);

            _originalStates.Clear();
            _committed = _textures.Count != 0;
        }
    }

    /// <summary>
    /// Restores the layout projection which existed before this recording.
    /// A transaction with later dependants cannot be abandoned first because
    /// their already-recorded barriers rely on its projected layouts.
    /// </summary>
    internal void Rollback()
    {
        VkTexture[] retainedTextures;
        lock (SyncRoot)
        {
            if (_committed)
            {
                throw new NeoVeldridException(
                    "A submitted Vulkan image-layout transaction cannot be rolled back.");
            }

            foreach (VkTexture texture in _textures)
                texture.ValidateRollingBackTransactionLocked(this);

            foreach (KeyValuePair<SubresourceKey, LayoutState> entry in _originalStates)
            {
                entry.Key.Texture.RestoreImageLayoutStateLocked(
                    entry.Key.Subresource,
                    entry.Value.Layout,
                    entry.Value.Revision);
            }

            foreach (VkTexture texture in _textures)
                texture.RollbackLayoutTransactionLocked(this);

            retainedTextures = _textures.ToArray();
            Clear();
        }

        for (int i = retainedTextures.Length - 1; i >= 0; i--)
            retainedTextures[i].RefCount.Decrement();
    }

    internal void ResetForReuse()
    {
        lock (SyncRoot)
        {
            if (_originalStates.Count != 0 || (!_committed && _textures.Count != 0))
            {
                throw new NeoVeldridException(
                    "A Vulkan image-layout transaction was recycled before it was committed or rolled back.");
            }
        }

        ReleaseCommittedResources();
    }

    internal void ReleaseCommittedResources()
    {
        VkTexture[] retainedTextures;
        lock (SyncRoot)
        {
            if (!_committed)
                return;

            retainedTextures = _textures.ToArray();
            Clear();
        }

        for (int i = retainedTextures.Length - 1; i >= 0; i--)
            retainedTextures[i].RefCount.Decrement();
    }

    private void Clear()
    {
        _originalStates.Clear();
        _textures.Clear();
        _committed = false;
        _recordingOrder = 0u;
    }

    private readonly record struct SubresourceKey(
        VkTexture Texture,
        uint Subresource);

    private readonly record struct LayoutState(
        ImageLayout Layout,
        uint Revision);
}
