using System;
using System.Collections.Generic;
using System.Diagnostics;
using Silk.NET.Direct3D11;

namespace NeoVeldrid.D3D11;

internal readonly record struct D3D11StagingBufferPoolDiagnostics(
    int PendingSubmissions, int RecordingBuffers, int AvailableBuffers,
    int SubmissionSlots, long CreatedBuffers, long CapacityWaits, long CapacityWaitTimestampTicks);

/// <summary>
/// CPU-write staging storage is reusable only after the GPU's copy has completed,
/// not after ExecuteCommandList returns. Completion ownership moves with one
/// recording; abandoning an unsubmitted recording needs no GPU wait.
/// </summary>
internal sealed unsafe class D3D11StagingBufferPool : IDisposable
{
    private enum SlotState { Available, Recording, Submitted }
    private sealed class Slot(D3D11Fence fence)
    {
        internal readonly D3D11Fence Fence = fence;
        internal readonly List<D3D11Buffer> Buffers = new();
        internal SlotState State;
    }

    private readonly D3D11GraphicsDevice _gd;
    private readonly int _maximumSubmissions;
    private readonly List<Slot> _slots = new();
    private readonly Queue<Slot> _pending = new();
    private readonly List<D3D11Buffer> _available = new();
    private Slot _recording;
    private long _createdBuffers, _capacityWaits, _capacityWaitTicks;

    internal D3D11StagingBufferPool(D3D11GraphicsDevice gd, uint maximumSubmissions)
    {
        _gd = gd;
        // Unspecified depth uses a small bounded backend default. Explicit
        // command-list depth (eight in the game's frame policy) remains authoritative.
        _maximumSubmissions = maximumSubmissions == 0 ? 3 : checked((int)maximumSubmissions);
    }

    internal D3D11StagingBufferPoolDiagnostics CaptureDiagnostics() => new(
        _pending.Count, _recording?.Buffers.Count ?? 0, _available.Count,
        _slots.Count, _createdBuffers, _capacityWaits, _capacityWaitTicks);

    internal D3D11Buffer Rent(uint sizeInBytes)
    {
        EnsureRecordingSlot();
        for (int i = 0; i < _available.Count; i++)
        {
            var buffer = _available[i];
            if (buffer.SizeInBytes < sizeInBytes) continue;
            _recording.Buffers.Add(buffer);
            _available.RemoveAt(i);
            return buffer;
        }
        var created = (D3D11Buffer)_gd.ResourceFactory.CreateBuffer(
            new BufferDescription(sizeInBytes, BufferUsage.Staging));
        try { _recording.Buffers.Add(created); }
        catch { created.Dispose(); throw; }
        _createdBuffers++;
        return created;
    }

    private void EnsureRecordingSlot()
    {
        if (_recording != null) return;
        ReclaimCompleted();
        if (_pending.Count == _maximumSubmissions)
        {
            _capacityWaits++;
            long started = Stopwatch.GetTimestamp();
            try { _gd.WaitForStagingRetirement(_pending.Peek().Fence); }
            finally { _capacityWaitTicks += Stopwatch.GetTimestamp() - started; }
            ReclaimCompleted();
        }
        foreach (var slot in _slots)
        {
            if (slot.State != SlotState.Available) continue;
            _recording = slot;
            break;
        }
        if (_recording == null)
        {
            if (_slots.Count >= _maximumSubmissions)
                throw new InvalidOperationException("A staging retirement did not release its submission slot.");
            var fence = new D3D11Fence(_gd, false);
            try
            {
                var slot = new Slot(fence);
                _slots.Add(slot);
                _recording = slot;
            }
            catch { fence.Dispose(); throw; }
        }
        // Allocate queue capacity before native submission commits.
        try { _pending.EnsureCapacity(_slots.Count); }
        catch { _recording = null; throw; }
        _recording.State = SlotState.Recording;
    }

    private void ReclaimCompleted()
    {
        while (_pending.Count != 0 && _pending.Peek().Fence.Signaled)
        {
            var slot = _pending.Peek();
            _available.EnsureCapacity(checked(_available.Count + slot.Buffers.Count));
            slot.Fence.Reset();
            ReturnBuffers(slot);
            _pending.Dequeue();
        }
    }

    internal void Submitted(ID3D11DeviceContext* context)
    {
        if (_recording == null) return;
        var slot = _recording;
        slot.Fence.AcquireSubmission(_gd);
        try { slot.Fence.ArmReserved(context); }
        finally { slot.Fence.ReleaseSubmission(); }
        slot.State = SlotState.Submitted;
        _pending.Enqueue(slot);
        _recording = null;
        // Presentation or an explicit capacity wait flushes the immediate
        // queue. Do not force another driver flush for every upload/submission.
    }

    internal void AbandonRecording()
    {
        if (_recording == null) return;
        ReturnBuffers(_recording);
        _recording = null;
    }

    private void ReturnBuffers(Slot slot)
    {
        _available.AddRange(slot.Buffers);
        slot.Buffers.Clear();
        slot.State = SlotState.Available;
    }

    public void Dispose()
    {
        // Native submitted commands retain their COM resources. Disposal drops
        // our references without waiting for the GPU or permitting later reuse.
        foreach (var slot in _slots)
        {
            foreach (var buffer in slot.Buffers) buffer.Dispose();
            slot.Buffers.Clear();
            slot.Fence.Dispose();
        }
        foreach (var buffer in _available) buffer.Dispose();
        _slots.Clear(); _pending.Clear(); _available.Clear(); _recording = null;
    }
}
