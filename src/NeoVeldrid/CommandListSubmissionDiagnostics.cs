using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NeoVeldrid;

/// <summary>
/// Identifies a buffer range consumed or produced by a recorded command.
/// </summary>
internal enum CommandListBufferAccessKind
{
    UpdateDestination,
    CopySourceAndDestination,
    DrawIndirectArguments,
    DrawIndexedIndirectArguments,
    DispatchIndirectArguments,
}

/// <summary>
/// Describes one buffer range access in the command list that reached a
/// successful graphics-device submission.
/// </summary>
internal readonly record struct CommandListBufferAccess(
    int CommandSequence,
    CommandListBufferAccessKind Kind,
    long PrimaryResourceIdentity,
    string PrimaryResourceName,
    ulong PrimaryOffsetInBytes,
    ulong SizeInBytes,
    long SecondaryResourceIdentity,
    string SecondaryResourceName,
    ulong SecondaryOffsetInBytes)
{
    public bool HasSecondaryResource => SecondaryResourceIdentity != 0L;
}

/// <summary>
/// Allocation-free summary of the last command recording that reached a
/// successful <see cref="GraphicsDevice.SubmitCommands(CommandList)"/> call.
/// </summary>
public readonly record struct CommandListSubmissionMetrics(
    long SubmissionSequence,
    int InstrumentedCommandCount,
    int DrawCallCount,
    int DrawIndexedCallCount,
    int DrawIndirectCallCount,
    int DrawIndexedIndirectCallCount,
    ulong DrawVertexCount,
    ulong DrawIndexCount,
    ulong DrawInstanceCount,
    int DispatchCallCount,
    int DispatchIndirectCallCount,
    int UpdateBufferCallCount,
    ulong UpdatedBufferBytes,
    int CopyBufferCallCount,
    ulong CopiedBufferBytes,
    int CopyTextureCallCount,
    int ResolveTextureCallCount,
    int GenerateMipmapsCallCount,
    int ClearColorTargetCallCount,
    int ClearDepthStencilCallCount,
    int SetFramebufferCallCount,
    int SetPipelineCallCount,
    int SetVertexBufferCallCount,
    int SetIndexBufferCallCount,
    int BufferAccessCount,
    ulong CommandSignature,
    int RenderCommandCount = 0,
    ulong RenderCommandSignature = 0UL,
    int SetGraphicsResourceSetCallCount = 0,
    int SetComputeResourceSetCallCount = 0,
    int SetViewportCallCount = 0,
    int SetScissorRectCallCount = 0)
{
    public int TotalDrawCallCount =>
        DrawCallCount +
        DrawIndexedCallCount +
        DrawIndirectCallCount +
        DrawIndexedIndirectCallCount;

    public int TotalDispatchCallCount =>
        DispatchCallCount + DispatchIndirectCallCount;
}

/// <summary>
/// Immutable metrics for one successful command-list submission.
/// </summary>
public sealed class CommandListSubmissionSnapshot
{
    private readonly CommandListBufferAccess[] _bufferAccesses;
    private readonly IReadOnlyList<CommandListBufferAccess> _bufferAccessesView;

    internal CommandListSubmissionSnapshot(
        CommandListSubmissionMetrics metrics,
        CommandListBufferAccess[] bufferAccesses)
    {
        Metrics = metrics;
        _bufferAccesses = bufferAccesses ?? Array.Empty<CommandListBufferAccess>();
        _bufferAccessesView = Array.AsReadOnly(_bufferAccesses);
    }

    public CommandListSubmissionMetrics Metrics { get; }

    internal IReadOnlyList<CommandListBufferAccess> BufferAccesses =>
        _bufferAccessesView;
}

/// <summary>
/// Opt-in command recording diagnostics owned by a single
/// <see cref="CommandList"/>. Normal command lists do not create an instance of
/// this class and pay only a null check at instrumented command boundaries.
/// Transfer metrics include instrumented backend expansions such as staged
/// buffer copies, so they are evidence for one backend's recording path rather
/// than cross-backend API call counts. The render signature excludes transfer
/// commands and normalizes frame-versioned resource sets by layout.
/// </summary>
internal sealed class CommandListSubmissionDiagnostics
{
    private const ulong SignatureOffsetBasis = 14695981039346656037UL;
    private const ulong SignaturePrime = 1099511628211UL;

    private List<CommandListBufferAccess> _recordingBufferAccesses;
    private List<CommandListBufferAccess> _submittedBufferAccesses;
    private CommandListSubmissionMetrics _lastSubmittedMetrics;
    private long _submissionSequence;
    private bool _isRecording;
    private bool _recordingEnded;
    private ulong _recordingSignature;
    private ulong _recordingRenderSignature;
    private bool _mixRenderCommand;
    private int _instrumentedCommandCount;
    private int _renderCommandCount;
    private int _drawCallCount;
    private int _drawIndexedCallCount;
    private int _drawIndirectCallCount;
    private int _drawIndexedIndirectCallCount;
    private ulong _drawVertexCount;
    private ulong _drawIndexCount;
    private ulong _drawInstanceCount;
    private int _dispatchCallCount;
    private int _dispatchIndirectCallCount;
    private int _updateBufferCallCount;
    private ulong _updatedBufferBytes;
    private int _copyBufferCallCount;
    private ulong _copiedBufferBytes;
    private int _copyTextureCallCount;
    private int _resolveTextureCallCount;
    private int _generateMipmapsCallCount;
    private int _clearColorTargetCallCount;
    private int _clearDepthStencilCallCount;
    private int _setFramebufferCallCount;
    private int _setPipelineCallCount;
    private int _setVertexBufferCallCount;
    private int _setIndexBufferCallCount;
    private int _setGraphicsResourceSetCallCount;
    private int _setComputeResourceSetCallCount;
    private int _setViewportCallCount;
    private int _setScissorRectCallCount;

    internal CommandListSubmissionDiagnostics(int initialBufferAccessCapacity)
    {
        if (initialBufferAccessCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(initialBufferAccessCapacity));

        _recordingBufferAccesses = new List<CommandListBufferAccess>(
            initialBufferAccessCapacity);
        _submittedBufferAccesses = new List<CommandListBufferAccess>(
            initialBufferAccessCapacity);
    }

    /// <summary>
    /// Gets the summary for the latest successfully submitted recording without
    /// allocating. The default value means no diagnosed submission has occurred.
    /// </summary>
    public CommandListSubmissionMetrics LastSubmittedMetrics =>
        _lastSubmittedMetrics;

    /// <summary>
    /// Copies the latest submitted recording so it remains stable while the
    /// command list is reused for later frames. Returns null until a recording
    /// reaches a successful submission.
    /// </summary>
    public CommandListSubmissionSnapshot CaptureLastSubmission()
    {
        if (_lastSubmittedMetrics.SubmissionSequence == 0)
        {
            return null;
        }

        var accesses = _submittedBufferAccesses.Count == 0
            ? Array.Empty<CommandListBufferAccess>()
            : _submittedBufferAccesses.ToArray();
        return new CommandListSubmissionSnapshot(_lastSubmittedMetrics, accesses);
    }

    internal void BeginRecording()
    {
        ResetRecordingMetrics();
        _recordingBufferAccesses.Clear();
        _isRecording = true;
        _recordingEnded = false;
    }

    internal void EndRecording()
    {
        if (!_isRecording)
            return;

        _isRecording = false;
        _recordingEnded = true;
    }

    internal void CompleteSuccessfulSubmission()
    {
        if (!_recordingEnded)
            return;

        _submissionSequence++;
        _lastSubmittedMetrics = new CommandListSubmissionMetrics(
            _submissionSequence,
            _instrumentedCommandCount,
            _drawCallCount,
            _drawIndexedCallCount,
            _drawIndirectCallCount,
            _drawIndexedIndirectCallCount,
            _drawVertexCount,
            _drawIndexCount,
            _drawInstanceCount,
            _dispatchCallCount,
            _dispatchIndirectCallCount,
            _updateBufferCallCount,
            _updatedBufferBytes,
            _copyBufferCallCount,
            _copiedBufferBytes,
            _copyTextureCallCount,
            _resolveTextureCallCount,
            _generateMipmapsCallCount,
            _clearColorTargetCallCount,
            _clearDepthStencilCallCount,
            _setFramebufferCallCount,
            _setPipelineCallCount,
            _setVertexBufferCallCount,
            _setIndexBufferCallCount,
            _recordingBufferAccesses.Count,
            _recordingSignature,
            _renderCommandCount,
            _recordingRenderSignature,
            _setGraphicsResourceSetCallCount,
            _setComputeResourceSetCallCount,
            _setViewportCallCount,
            _setScissorRectCallCount);

        (_recordingBufferAccesses, _submittedBufferAccesses) =
            (_submittedBufferAccesses, _recordingBufferAccesses);
        _recordingBufferAccesses.Clear();
        _recordingEnded = false;
    }

    internal void RecordSetFramebuffer(Framebuffer framebuffer)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.SetFramebuffer);
        _setFramebufferCallCount++;
        MixResource(framebuffer);
    }

    internal void RecordSetPipeline(Pipeline pipeline)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.SetPipeline);
        _setPipelineCallCount++;
        MixResource(pipeline);
    }

    internal void RecordSetVertexBuffer(
        uint index,
        DeviceBuffer buffer,
        uint offset)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.SetVertexBuffer);
        _setVertexBufferCallCount++;
        Mix(index);
        MixResource(buffer);
        Mix(offset);
    }

    internal void RecordSetIndexBuffer(
        DeviceBuffer buffer,
        IndexFormat format,
        uint offset)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.SetIndexBuffer);
        _setIndexBufferCallCount++;
        MixResource(buffer);
        Mix((uint)format);
        Mix(offset);
    }

    internal void RecordSetGraphicsResourceSet(
        uint slot,
        ResourceSet resourceSet,
        uint dynamicOffsetsCount,
        ref uint dynamicOffsets)
    {
        if (!_isRecording)
            return;

        RecordSetResourceSet(
            CommandKind.SetGraphicsResourceSet,
            slot,
            resourceSet,
            dynamicOffsetsCount,
            ref dynamicOffsets);
        _setGraphicsResourceSetCallCount++;
    }

    internal void RecordSetComputeResourceSet(
        uint slot,
        ResourceSet resourceSet,
        uint dynamicOffsetsCount,
        ref uint dynamicOffsets)
    {
        if (!_isRecording)
            return;

        RecordSetResourceSet(
            CommandKind.SetComputeResourceSet,
            slot,
            resourceSet,
            dynamicOffsetsCount,
            ref dynamicOffsets);
        _setComputeResourceSetCallCount++;
    }

    internal void RecordSetViewport(uint index, in Viewport viewport)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.SetViewport);
        _setViewportCallCount++;
        Mix(index);
        Mix(BitConverter.SingleToUInt32Bits(viewport.X));
        Mix(BitConverter.SingleToUInt32Bits(viewport.Y));
        Mix(BitConverter.SingleToUInt32Bits(viewport.Width));
        Mix(BitConverter.SingleToUInt32Bits(viewport.Height));
        Mix(BitConverter.SingleToUInt32Bits(viewport.MinDepth));
        Mix(BitConverter.SingleToUInt32Bits(viewport.MaxDepth));
    }

    internal void RecordSetScissorRect(
        uint index,
        uint x,
        uint y,
        uint width,
        uint height)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.SetScissorRect);
        _setScissorRectCallCount++;
        Mix(index);
        Mix(x);
        Mix(y);
        Mix(width);
        Mix(height);
    }

    internal void RecordClearColorTarget(uint index, RgbaFloat clearColor)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.ClearColorTarget);
        _clearColorTargetCallCount++;
        Mix(index);
        Mix(BitConverter.SingleToUInt32Bits(clearColor.R));
        Mix(BitConverter.SingleToUInt32Bits(clearColor.G));
        Mix(BitConverter.SingleToUInt32Bits(clearColor.B));
        Mix(BitConverter.SingleToUInt32Bits(clearColor.A));
    }

    internal void RecordClearDepthStencil(float depth, byte stencil)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.ClearDepthStencil);
        _clearDepthStencilCallCount++;
        Mix(BitConverter.SingleToUInt32Bits(depth));
        Mix(stencil);
    }

    internal void RecordDraw(
        uint vertexCount,
        uint instanceCount,
        uint vertexStart,
        uint instanceStart)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.Draw);
        _drawCallCount++;
        _drawVertexCount += (ulong)vertexCount * instanceCount;
        _drawInstanceCount += instanceCount;
        Mix(vertexCount);
        Mix(instanceCount);
        Mix(vertexStart);
        Mix(instanceStart);
    }

    internal void RecordDrawIndexed(
        uint indexCount,
        uint instanceCount,
        uint indexStart,
        int vertexOffset,
        uint instanceStart)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.DrawIndexed);
        _drawIndexedCallCount++;
        _drawIndexCount += (ulong)indexCount * instanceCount;
        _drawInstanceCount += instanceCount;
        Mix(indexCount);
        Mix(instanceCount);
        Mix(indexStart);
        Mix(unchecked((uint)vertexOffset));
        Mix(instanceStart);
    }

    internal void RecordDrawIndirect(
        DeviceBuffer indirectBuffer,
        uint offset,
        uint drawCount,
        uint stride,
        bool indexed)
    {
        if (!_isRecording)
            return;

        CommandKind commandKind = indexed
            ? CommandKind.DrawIndexedIndirect
            : CommandKind.DrawIndirect;
        BeginCommand(commandKind);
        if (indexed)
            _drawIndexedIndirectCallCount++;
        else
            _drawIndirectCallCount++;
        MixResource(indirectBuffer);
        Mix(offset);
        Mix(drawCount);
        Mix(stride);
        AddBufferAccess(
            indexed
                ? CommandListBufferAccessKind.DrawIndexedIndirectArguments
                : CommandListBufferAccessKind.DrawIndirectArguments,
            indirectBuffer,
            offset,
            CalculateStridedAccessSize(
                drawCount,
                stride,
                indexed
                    ? (uint)Unsafe.SizeOf<IndirectDrawIndexedArguments>()
                    : (uint)Unsafe.SizeOf<IndirectDrawArguments>()));
    }

    internal void RecordDispatch(
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.Dispatch);
        _dispatchCallCount++;
        Mix(groupCountX);
        Mix(groupCountY);
        Mix(groupCountZ);
    }

    internal void RecordDispatchIndirect(
        DeviceBuffer indirectBuffer,
        uint offset,
        uint argumentSizeInBytes)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.DispatchIndirect);
        _dispatchIndirectCallCount++;
        MixResource(indirectBuffer);
        Mix(offset);
        Mix(argumentSizeInBytes);
        AddBufferAccess(
            CommandListBufferAccessKind.DispatchIndirectArguments,
            indirectBuffer,
            offset,
            argumentSizeInBytes);
    }

    internal void RecordUpdateBuffer(
        DeviceBuffer buffer,
        uint offset,
        uint sizeInBytes)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.UpdateBuffer);
        _updateBufferCallCount++;
        _updatedBufferBytes += sizeInBytes;
        MixResource(buffer);
        Mix(offset);
        Mix(sizeInBytes);
        AddBufferAccess(
            CommandListBufferAccessKind.UpdateDestination,
            buffer,
            offset,
            sizeInBytes);
    }

    internal void RecordCopyBuffer(
        DeviceBuffer source,
        uint sourceOffset,
        DeviceBuffer destination,
        uint destinationOffset,
        uint sizeInBytes)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.CopyBuffer);
        _copyBufferCallCount++;
        _copiedBufferBytes += sizeInBytes;
        MixResource(source);
        Mix(sourceOffset);
        MixResource(destination);
        Mix(destinationOffset);
        Mix(sizeInBytes);
        AddBufferAccess(
            CommandListBufferAccessKind.CopySourceAndDestination,
            source,
            sourceOffset,
            sizeInBytes,
            destination,
            destinationOffset);
    }

    internal void RecordCopyTexture(
        Texture source,
        uint srcX,
        uint srcY,
        uint srcZ,
        uint srcMipLevel,
        uint srcBaseArrayLayer,
        Texture destination,
        uint dstX,
        uint dstY,
        uint dstZ,
        uint dstMipLevel,
        uint dstBaseArrayLayer,
        uint width,
        uint height,
        uint depth,
        uint layerCount)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.CopyTexture);
        _copyTextureCallCount++;
        MixResource(source);
        Mix(srcX);
        Mix(srcY);
        Mix(srcZ);
        Mix(srcMipLevel);
        Mix(srcBaseArrayLayer);
        MixResource(destination);
        Mix(dstX);
        Mix(dstY);
        Mix(dstZ);
        Mix(dstMipLevel);
        Mix(dstBaseArrayLayer);
        Mix(width);
        Mix(height);
        Mix(depth);
        Mix(layerCount);
    }

    internal void RecordResolveTexture(Texture source, Texture destination)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.ResolveTexture);
        _resolveTextureCallCount++;
        MixResource(source);
        MixResource(destination);
    }

    internal void RecordGenerateMipmaps(Texture texture)
    {
        if (!_isRecording)
            return;

        BeginCommand(CommandKind.GenerateMipmaps);
        _generateMipmapsCallCount++;
        MixResource(texture);
    }

    private void BeginCommand(CommandKind kind)
    {
        _instrumentedCommandCount++;
        _mixRenderCommand = IsRenderCommand(kind);
        if (_mixRenderCommand)
            _renderCommandCount++;
        Mix((uint)kind);
    }

    private static bool IsRenderCommand(CommandKind kind)
        => kind != CommandKind.UpdateBuffer &&
           kind != CommandKind.CopyBuffer &&
           kind != CommandKind.CopyTexture &&
           kind != CommandKind.ResolveTexture &&
           kind != CommandKind.GenerateMipmaps;

    private void RecordSetResourceSet(
        CommandKind kind,
        uint slot,
        ResourceSet resourceSet,
        uint dynamicOffsetsCount,
        ref uint dynamicOffsets)
    {
        BeginCommand(kind);
        Mix(slot);

        // A frame-versioned resource set can be a different object on each
        // bounded submission slot while representing the same binding
        // topology. Preserve the concrete set identity in the full signature,
        // while the render signature follows the authoritative layout
        // identity. Slot, offset count, and every dynamic offset remain part of
        // both signatures.
        MixFullResource(resourceSet);
        MixRenderResource(resourceSet.Layout);
        Mix(dynamicOffsetsCount);
        for (uint i = 0; i < dynamicOffsetsCount; i++)
            Mix(Unsafe.Add(ref dynamicOffsets, checked((int)i)));
    }

    private void AddBufferAccess(
        CommandListBufferAccessKind kind,
        DeviceBuffer primary,
        ulong primaryOffset,
        ulong sizeInBytes,
        DeviceBuffer secondary = null,
        ulong secondaryOffset = 0UL)
    {
        _recordingBufferAccesses.Add(new CommandListBufferAccess(
            _instrumentedCommandCount,
            kind,
            CommandListDiagnosticResourceIdentity.Get(primary),
            primary.Name,
            primaryOffset,
            sizeInBytes,
            secondary is null
                ? 0L
                : CommandListDiagnosticResourceIdentity.Get(secondary),
            secondary?.Name,
            secondaryOffset));
    }

    private static ulong CalculateStridedAccessSize(
        uint elementCount,
        uint stride,
        uint elementSizeInBytes)
        => elementCount == 0u
            ? 0UL
            : (ulong)(elementCount - 1u) * stride + elementSizeInBytes;

    private void ResetRecordingMetrics()
    {
        _recordingSignature = SignatureOffsetBasis;
        _recordingRenderSignature = SignatureOffsetBasis;
        _mixRenderCommand = false;
        _instrumentedCommandCount = 0;
        _renderCommandCount = 0;
        _drawCallCount = 0;
        _drawIndexedCallCount = 0;
        _drawIndirectCallCount = 0;
        _drawIndexedIndirectCallCount = 0;
        _drawVertexCount = 0UL;
        _drawIndexCount = 0UL;
        _drawInstanceCount = 0UL;
        _dispatchCallCount = 0;
        _dispatchIndirectCallCount = 0;
        _updateBufferCallCount = 0;
        _updatedBufferBytes = 0UL;
        _copyBufferCallCount = 0;
        _copiedBufferBytes = 0UL;
        _copyTextureCallCount = 0;
        _resolveTextureCallCount = 0;
        _generateMipmapsCallCount = 0;
        _clearColorTargetCallCount = 0;
        _clearDepthStencilCallCount = 0;
        _setFramebufferCallCount = 0;
        _setPipelineCallCount = 0;
        _setVertexBufferCallCount = 0;
        _setIndexBufferCallCount = 0;
        _setGraphicsResourceSetCallCount = 0;
        _setComputeResourceSetCallCount = 0;
        _setViewportCallCount = 0;
        _setScissorRectCallCount = 0;
    }

    private void MixResource(DeviceResource resource)
    {
        Mix(unchecked((ulong)CommandListDiagnosticResourceIdentity.Get(resource)));
    }

    private void MixFullResource(DeviceResource resource)
    {
        MixFull(unchecked((ulong)CommandListDiagnosticResourceIdentity.Get(resource)));
    }

    private void MixRenderResource(DeviceResource resource)
    {
        MixRender(unchecked((ulong)CommandListDiagnosticResourceIdentity.Get(resource)));
    }

    private void Mix(uint value) => Mix((ulong)value);

    private void Mix(byte value) => Mix((ulong)value);

    private void Mix(ulong value)
    {
        MixFull(value);
        if (_mixRenderCommand)
            MixRender(value);
    }

    private void MixFull(ulong value)
    {
        unchecked
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                byte next = (byte)(value >> shift);
                _recordingSignature ^= next;
                _recordingSignature *= SignaturePrime;
            }
        }
    }

    private void MixRender(ulong value)
    {
        unchecked
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                byte next = (byte)(value >> shift);
                _recordingRenderSignature ^= next;
                _recordingRenderSignature *= SignaturePrime;
            }
        }
    }

    private enum CommandKind : uint
    {
        SetFramebuffer = 1,
        SetPipeline,
        SetVertexBuffer,
        SetIndexBuffer,
        ClearColorTarget,
        ClearDepthStencil,
        Draw,
        DrawIndexed,
        DrawIndirect,
        DrawIndexedIndirect,
        Dispatch,
        DispatchIndirect,
        UpdateBuffer,
        CopyBuffer,
        CopyTexture,
        ResolveTexture,
        GenerateMipmaps,
        SetGraphicsResourceSet,
        SetComputeResourceSet,
        SetViewport,
        SetScissorRect,
    }
}

internal static class CommandListDiagnosticResourceIdentity
{
    private static readonly ConditionalWeakTable<DeviceResource, ResourceIdentity>
        Identities = new();
    private static long _nextIdentity;

    public static long Get(DeviceResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Identities.GetValue(
            resource,
            static _ => new ResourceIdentity(
                Interlocked.Increment(ref _nextIdentity))).Value;
    }

    private sealed class ResourceIdentity(long value)
    {
        public long Value { get; } = value;
    }
}
