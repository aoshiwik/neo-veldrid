using System;
using Silk.NET.Vulkan;
using static NeoVeldrid.Vk.VulkanUtil;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text;

using VkApi = Silk.NET.Vulkan.Vk;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;
using VkImageHandle = Silk.NET.Vulkan.Image;

namespace NeoVeldrid.Vk;

internal unsafe class VkCommandList : CommandList
{
    private const int RetainedStagingBufferCapacity = 4;
    private const uint DefaultStagingUploadPageSize = 256u * 1024u;
    private const uint BufferCopyAlignment = 4u;

    private readonly VkGraphicsDevice _gd;
    private CommandPool _pool;
    private CommandBuffer _cb;
    private bool _destroyed;

    private bool _commandBufferBegun;
    private bool _commandBufferEnded;
    private Rect2D[] _scissorRects = Array.Empty<Rect2D>();

    private ClearValue[] _clearValues = Array.Empty<ClearValue>();
    private bool[] _validColorClearValues = Array.Empty<bool>();
    private ClearValue? _depthClearValue;
    private readonly List<VkTexture> _preDrawSampledImages = new List<VkTexture>();

    // Graphics State
    private VkFramebufferBase _currentFramebuffer;
    private bool _currentFramebufferEverActive;
    private RenderPass _activeRenderPass;
    private VkPipeline _currentGraphicsPipeline;
    private BoundResourceSetInfo[] _currentGraphicsResourceSets = Array.Empty<BoundResourceSetInfo>();
    private bool[] _graphicsResourceSetsChanged;

    private bool _newFramebuffer; // Render pass cycle state

    // Compute State
    private VkPipeline _currentComputePipeline;
    private BoundResourceSetInfo[] _currentComputeResourceSets = Array.Empty<BoundResourceSetInfo>();
    private bool[] _computeResourceSetsChanged;
    private string _name;

    private readonly object _commandBufferListLock = new object();
    private readonly Queue<CommandBuffer> _availableCommandBuffers;
    private readonly List<CommandBuffer> _submittedCommandBuffers;

    private StagingResourceInfo _currentStagingInfo;
    private readonly object _stagingLock = new object();
    private readonly Dictionary<CommandBuffer, StagingResourceInfo> _submittedStagingInfos;
    private readonly List<StagingResourceInfo> _availableStagingInfos;
    private readonly List<VkBuffer> _availableStagingBuffers;
    private readonly int _maximumInFlightSubmissionCount;
    private readonly int _initialTrackedResourceCapacityPerSubmission;
    private readonly uint _initialStagingUploadPageSize;

    public CommandPool CommandPool => _pool;
    public CommandBuffer CommandBuffer => _cb;

    public ResourceRefCount RefCount { get; }

    public override bool IsDisposed => _destroyed;

    public VkCommandList(VkGraphicsDevice gd, ref CommandListDescription description)
        : base(ref description, gd, gd.Features, gd.UniformBufferMinOffsetAlignment, gd.StructuredBufferMinOffsetAlignment)
    {
        _gd = gd;
        _maximumInFlightSubmissionCount = ResolveCapacity(
            description.MaximumInFlightSubmissionCount,
            nameof(description.MaximumInFlightSubmissionCount));
        if (_maximumInFlightSubmissionCount > 0)
        {
            ConfigureRecordingSubmissionSlots(
                checked((uint)_maximumInFlightSubmissionCount));
        }
        _initialTrackedResourceCapacityPerSubmission = ResolveCapacity(
            description.InitialTrackedResourceCapacityPerSubmission,
            nameof(description.InitialTrackedResourceCapacityPerSubmission));
        _initialStagingUploadPageSize =
            description.InitialStagingUploadPageSize;
        _availableCommandBuffers =
            new Queue<CommandBuffer>(_maximumInFlightSubmissionCount);
        _submittedCommandBuffers =
            new List<CommandBuffer>(_maximumInFlightSubmissionCount);
        _submittedStagingInfos = new Dictionary<CommandBuffer, StagingResourceInfo>(
            _maximumInFlightSubmissionCount,
            CommandBufferHandleComparer.Instance);
        _availableStagingInfos =
            new List<StagingResourceInfo>(_maximumInFlightSubmissionCount);
        _availableStagingBuffers =
            new List<VkBuffer>(Math.Max(
                RetainedStagingBufferCapacity,
                _maximumInFlightSubmissionCount));
        for (int i = 0; i < _maximumInFlightSubmissionCount; i++)
        {
            _availableStagingInfos.Add(new StagingResourceInfo(
                _initialTrackedResourceCapacityPerSubmission,
                (uint)i));
        }

        CommandPoolCreateInfo poolCI = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = gd.GraphicsQueueIndex
        };
        Result result = _gd.Vk.CreateCommandPool(_gd.Device, in poolCI, null, out _pool);
        CheckResult(result);
        try
        {
            int retainedUploadPageCount =
                _initialStagingUploadPageSize == 0u
                    ? 0
                    : Math.Max(1, _maximumInFlightSubmissionCount);
            for (int i = 0; i < retainedUploadPageCount; i++)
            {
                VkBuffer uploadPage =
                    (VkBuffer)_gd.ResourceFactory.CreateBuffer(
                        new BufferDescription(
                            _initialStagingUploadPageSize,
                            BufferUsage.Staging));
                _availableStagingBuffers.Add(uploadPage);
                uploadPage.Name =
                    $"Retained Upload Page {i} (CommandList {_name})";
            }

            _cb = GetNextCommandBuffer();
            RefCount = new ResourceRefCount(DisposeCore);
        }
        catch
        {
            foreach (VkBuffer uploadPage in _availableStagingBuffers)
                uploadPage.Dispose();
            _availableStagingBuffers.Clear();
            _gd.Vk.DestroyCommandPool(_gd.Device, _pool, null);
            _pool = default;
            throw;
        }
    }

    private static int ResolveCapacity(uint value, string parameterName)
    {
        if (value > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName);

        return (int)value;
    }

    private CommandBuffer GetNextCommandBuffer()
    {
        lock (_commandBufferListLock)
        {
            if (_availableCommandBuffers.Count > 0)
            {
                CommandBuffer cachedCB = _availableCommandBuffers.Dequeue();
                Result resetResult = _gd.Vk.ResetCommandBuffer(cachedCB, 0);
                CheckResult(resetResult);
                return cachedCB;
            }
        }

        CommandBufferAllocateInfo cbAI = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            CommandBufferCount = 1,
            Level = CommandBufferLevel.Primary
        };
        Result result = _gd.Vk.AllocateCommandBuffers(_gd.Device, in cbAI, out CommandBuffer cb);
        CheckResult(result);
        return cb;
    }

    public void CommandBufferSubmitted(CommandBuffer cb)
    {
        StagingResourceInfo info = _currentStagingInfo;
        info.ImageLayouts.ValidateSubmissionOrder();
        foreach (VkMappableResourceSubmissionAccess access in info.SubmissionAccesses)
        {
            try
            {
                access.BeginSubmissionUse();
                info.AcquiredSubmissionAccesses.Add(access);
            }
            catch
            {
                for (int i = info.AcquiredSubmissionAccesses.Count - 1; i >= 0; i--)
                {
                    info.AcquiredSubmissionAccesses[i].EndSubmissionUse();
                }

                info.AcquiredSubmissionAccesses.Clear();
                throw;
            }
        }

        RefCount.Increment();
        foreach (ResourceRefCount rrc in info.Resources)
        {
            rrc.Increment();
        }
        info.SubmissionReferencesAcquired = true;

        try
        {
            AcquireObservedTextureUploadRetentions(info);
            lock (_stagingLock)
            {
                _submittedStagingInfos.Add(cb, info);
            }
            _currentStagingInfo = null;
        }
        catch
        {
            if (ReleaseSubmissionResourceReferences(info))
                RefCount.Decrement();
            throw;
        }
    }

    public void CommandBufferSubmissionSucceeded(CommandBuffer cb)
    {
        StagingResourceInfo info;
        lock (_stagingLock)
        {
            if (!_submittedStagingInfos.TryGetValue(cb, out info))
            {
                throw new NeoVeldridException(
                    "A successful Vulkan command-buffer submission had no prepared resource transaction.");
            }
        }

        info.ImageLayouts.CommitAfterSubmission();
    }

    public void CommandBufferSubmissionFailed(CommandBuffer cb)
    {
        StagingResourceInfo info;
        bool releaseCommandListReference;
        lock (_stagingLock)
        {
            if (_currentStagingInfo != null)
            {
                throw new NeoVeldridException(
                    "A failed Vulkan submission cannot restore over an active recording transaction.");
            }

            if (!_submittedStagingInfos.Remove(cb, out info))
            {
                throw new NeoVeldridException(
                    "A failed Vulkan command-buffer submission had no prepared resource transaction.");
            }

            releaseCommandListReference =
                ReleaseSubmissionResourceReferences(info);

            // Keep recorded resources and staging pages intact so the ended command
            // buffer can be submitted again after a recoverable queue failure.
            _currentStagingInfo = info;
        }

        if (releaseCommandListReference)
            RefCount.Decrement();
    }

    public void CommandBufferCompleted(CommandBuffer completedCB)
    {
        lock (_commandBufferListLock)
        {
            for (int i = 0; i < _submittedCommandBuffers.Count; i++)
            {
                CommandBuffer submittedCB = _submittedCommandBuffers[i];
                if (submittedCB.Handle == completedCB.Handle)
                {
                    _availableCommandBuffers.Enqueue(completedCB);
                    _submittedCommandBuffers.RemoveAt(i);
                    i -= 1;
                }
            }
        }

        StagingResourceInfo completedInfo = null;
        lock (_stagingLock)
        {
            _submittedStagingInfos.Remove(completedCB, out completedInfo);
        }

        if (completedInfo != null)
            RecycleStagingInfo(completedInfo);
    }

    private bool ReleaseSubmissionResourceReferences(StagingResourceInfo info)
    {
        if (!info.SubmissionReferencesAcquired)
            return false;

        info.SubmissionReferencesAcquired = false;
        for (int i = info.AcquiredSubmissionAccesses.Count - 1; i >= 0; i--)
            info.AcquiredSubmissionAccesses[i].EndSubmissionUse();
        info.AcquiredSubmissionAccesses.Clear();

        foreach (ResourceRefCount resource in info.Resources)
            resource.Decrement();
        ReleaseObservedTextureUploadRetentions(info);
        return true;
    }

    private protected override void BeginCore()
    {
        if (_commandBufferBegun)
        {
            throw new NeoVeldridException(
                "CommandList must be in its initial state, or End() must have been called, for Begin() to be valid to call.");
        }
        if (_commandBufferEnded)
        {
            if (_currentStagingInfo != null)
            {
                _currentStagingInfo.ImageLayouts.Rollback();
                RecycleAbandonedEndedCommandBuffer(_cb);
                RecycleStagingInfo(_currentStagingInfo);
            }

            _commandBufferEnded = false;
            _currentStagingInfo = GetStagingResourceInfo();
            _cb = GetNextCommandBuffer();
        }
        else
        {
            _currentStagingInfo = GetStagingResourceInfo();
        }

        if (RecordingSubmissionSlotCount != 0u)
            SetRecordingSubmissionSlot(_currentStagingInfo.Slot);

        CommandBufferBeginInfo beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        Result result = _gd.Vk.BeginCommandBuffer(_cb, in beginInfo);
        CheckResult(result);
        _commandBufferBegun = true;

        ClearCachedState();
        _currentFramebuffer = null;
        _currentGraphicsPipeline = null;
        ClearSets(_currentGraphicsResourceSets);
        Util.ClearArray(_scissorRects);

        _currentComputePipeline = null;
        ClearSets(_currentComputeResourceSets);
    }

    private void RecycleAbandonedEndedCommandBuffer(CommandBuffer commandBuffer)
    {
        lock (_commandBufferListLock)
        {
            for (int i = 0; i < _submittedCommandBuffers.Count; i++)
            {
                if (_submittedCommandBuffers[i].Handle != commandBuffer.Handle)
                    continue;

                _submittedCommandBuffers.RemoveAt(i);
                _availableCommandBuffers.Enqueue(commandBuffer);
                return;
            }
        }

        throw new NeoVeldridException(
            "An abandoned Vulkan command buffer was not owned by its command list.");
    }

    private protected override void ClearColorTargetCore(uint index, RgbaFloat clearColor)
    {
        ClearValue clearValue = new ClearValue
        {
            Color = new ClearColorValue(clearColor.R, clearColor.G, clearColor.B, clearColor.A)
        };

        if (_activeRenderPass.Handle != default)
        {
            ClearAttachment clearAttachment = new ClearAttachment
            {
                ColorAttachment = index,
                AspectMask = ImageAspectFlags.ColorBit,
                ClearValue = clearValue
            };

            Texture colorTex = _currentFramebuffer.ColorTargets[(int)index].Target;
            ClearRect clearRect = new ClearRect
            {
                BaseArrayLayer = 0,
                LayerCount = 1,
                Rect = new Rect2D(new Offset2D(0, 0), new Extent2D(colorTex.Width, colorTex.Height))
            };

            _gd.Vk.CmdClearAttachments(_cb, 1, in clearAttachment, 1, in clearRect);
        }
        else
        {
            // Queue up the clear value for the next RenderPass.
            _clearValues[index] = clearValue;
            _validColorClearValues[index] = true;
        }
    }

    private protected override void ClearDepthStencilCore(float depth, byte stencil)
    {
        ClearValue clearValue = new ClearValue
        {
            DepthStencil = new ClearDepthStencilValue(depth, stencil)
        };

        if (_activeRenderPass.Handle != default)
        {
            ImageAspectFlags aspect = FormatHelpers.IsStencilFormat(_currentFramebuffer.DepthTarget.Value.Target.Format)
                ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                : ImageAspectFlags.DepthBit;
            ClearAttachment clearAttachment = new ClearAttachment
            {
                AspectMask = aspect,
                ClearValue = clearValue
            };

            uint renderableWidth = _currentFramebuffer.RenderableWidth;
            uint renderableHeight = _currentFramebuffer.RenderableHeight;
            if (renderableWidth > 0 && renderableHeight > 0)
            {
                ClearRect clearRect = new ClearRect
                {
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                    Rect = new Rect2D(new Offset2D(0, 0), new Extent2D(renderableWidth, renderableHeight))
                };

                _gd.Vk.CmdClearAttachments(_cb, 1, in clearAttachment, 1, in clearRect);
            }
        }
        else
        {
            // Queue up the clear value for the next RenderPass.
            _depthClearValue = clearValue;
        }
    }

    private protected override void DrawCore(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
    {
        PreDrawCommand();
        _gd.Vk.CmdDraw(_cb, vertexCount, instanceCount, vertexStart, instanceStart);
    }

    private protected override void DrawIndexedCore(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
    {
        PreDrawCommand();
        _gd.Vk.CmdDrawIndexed(_cb, indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
    }

    private protected override void DrawIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
    {
        PreDrawCommand();
        VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(indirectBuffer);
        TrackBuffer(vkBuffer);
        _gd.Vk.CmdDrawIndirect(_cb, vkBuffer.DeviceBuffer, offset, drawCount, stride);
    }

    private protected override void DrawIndexedIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
    {
        PreDrawCommand();
        VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(indirectBuffer);
        TrackBuffer(vkBuffer);
        _gd.Vk.CmdDrawIndexedIndirect(_cb, vkBuffer.DeviceBuffer, offset, drawCount, stride);
    }

    private void PreDrawCommand()
    {
        TransitionImages(_preDrawSampledImages, ImageLayout.ShaderReadOnlyOptimal);
        _preDrawSampledImages.Clear();

        EnsureRenderPassActive();

        FlushNewResourceSets(
            _currentGraphicsResourceSets,
            _graphicsResourceSetsChanged,
            _currentGraphicsPipeline.ResourceSetCount,
            PipelineBindPoint.Graphics,
            _currentGraphicsPipeline.PipelineLayout);
    }

    private void FlushNewResourceSets(
        BoundResourceSetInfo[] resourceSets,
        bool[] resourceSetsChanged,
        uint resourceSetCount,
        PipelineBindPoint bindPoint,
        Silk.NET.Vulkan.PipelineLayout pipelineLayout)
    {
        VkPipeline pipeline = bindPoint == PipelineBindPoint.Graphics ? _currentGraphicsPipeline : _currentComputePipeline;

        DescriptorSet* descriptorSets = stackalloc DescriptorSet[(int)resourceSetCount];
        uint* dynamicOffsets = stackalloc uint[pipeline.DynamicOffsetsCount];
        uint currentBatchCount = 0;
        uint currentBatchFirstSet = 0;
        uint currentBatchDynamicOffsetCount = 0;

        for (uint currentSlot = 0; currentSlot < resourceSetCount; currentSlot++)
        {
            bool batchEnded = !resourceSetsChanged[currentSlot] || currentSlot == resourceSetCount - 1;

            if (resourceSetsChanged[currentSlot])
            {
                resourceSetsChanged[currentSlot] = false;
                VkResourceSet vkSet = Util.AssertSubtype<ResourceSet, VkResourceSet>(resourceSets[currentSlot].Set);
                descriptorSets[currentBatchCount] = vkSet.DescriptorSet;
                currentBatchCount += 1;

                ref SmallFixedOrDynamicArray curSetOffsets = ref resourceSets[currentSlot].Offsets;
                for (uint i = 0; i < curSetOffsets.Count; i++)
                {
                    dynamicOffsets[currentBatchDynamicOffsetCount] = curSetOffsets.Get(i);
                    currentBatchDynamicOffsetCount += 1;
                }

                // Increment ref count on first use of a set.
                _currentStagingInfo.Resources.Add(vkSet.RefCount);
                for (int i = 0; i < vkSet.RefCounts.Count; i++)
                {
                    _currentStagingInfo.Resources.Add(vkSet.RefCounts[i]);
                }
                for (int i = 0; i < vkSet.Buffers.Count; i++)
                {
                    TrackBuffer(vkSet.Buffers[i]);
                }
            }

            if (batchEnded)
            {
                if (currentBatchCount != 0)
                {
                    // Flush current batch.
                    _gd.Vk.CmdBindDescriptorSets(
                        _cb,
                        bindPoint,
                        pipelineLayout,
                        currentBatchFirstSet,
                        currentBatchCount,
                        descriptorSets,
                        currentBatchDynamicOffsetCount,
                        dynamicOffsets);
                }

                currentBatchCount = 0;
                currentBatchFirstSet = currentSlot + 1;
            }
        }
    }

    private void TransitionImages(List<VkTexture> sampledTextures, ImageLayout layout)
    {
        for (int i = 0; i < sampledTextures.Count; i++)
        {
            VkTexture tex = sampledTextures[i];
            tex.TransitionImageLayout(
                _cb,
                0,
                tex.MipLevels,
                0,
                tex.ActualArrayLayers,
                layout,
                _currentStagingInfo.ImageLayouts);
        }
    }

    private protected override void DispatchCore(
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ)
    {
        PreDispatchCommand();

        _gd.Vk.CmdDispatch(_cb, groupCountX, groupCountY, groupCountZ);
    }

    private void PreDispatchCommand()
    {
        EnsureNoRenderPass();

        for (uint currentSlot = 0; currentSlot < _currentComputePipeline.ResourceSetCount; currentSlot++)
        {
            VkResourceSet vkSet = Util.AssertSubtype<ResourceSet, VkResourceSet>(
                _currentComputeResourceSets[currentSlot].Set);

            TransitionImages(vkSet.SampledTextures, ImageLayout.ShaderReadOnlyOptimal);
            TransitionImages(vkSet.StorageTextures, ImageLayout.General);
            for (int texIdx = 0; texIdx < vkSet.StorageTextures.Count; texIdx++)
            {
                VkTexture storageTex = vkSet.StorageTextures[texIdx];
                if ((storageTex.Usage & TextureUsage.Sampled) != 0)
                {
                    _preDrawSampledImages.Add(storageTex);
                }
            }
        }

        FlushNewResourceSets(
            _currentComputeResourceSets,
            _computeResourceSetsChanged,
            _currentComputePipeline.ResourceSetCount,
            PipelineBindPoint.Compute,
            _currentComputePipeline.PipelineLayout);
    }

    private protected override void DispatchIndirectCore(DeviceBuffer indirectBuffer, uint offset)
    {
        PreDispatchCommand();

        VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(indirectBuffer);
        TrackBuffer(vkBuffer);
        _gd.Vk.CmdDispatchIndirect(_cb, vkBuffer.DeviceBuffer, offset);
    }

    protected override void ResolveTextureCore(Texture source, Texture destination)
    {
        EnsureNoRenderPass();

        VkTexture vkSource = Util.AssertSubtype<Texture, VkTexture>(source);
        VkTexture vkDestination = Util.AssertSubtype<Texture, VkTexture>(destination);
        TrackTexture(vkSource);
        TrackTexture(vkDestination);

        VkImageTransferLayout.Access sourceAccess =
            VkImageTransferLayout.PrepareForRead(
                _cb,
                vkSource,
                0,
                1,
                0,
                1,
                _currentStagingInfo.ImageLayouts);
        VkImageTransferLayout.Access destinationAccess =
            VkImageTransferLayout.PrepareForWrite(
                _gd.Vk,
                _cb,
                vkDestination,
                0,
                1,
                0,
                1,
                _currentStagingInfo.ImageLayouts);

        ImageResolve region = new ImageResolve
        {
            Extent = new Extent3D { Width = source.Width, Height = source.Height, Depth = source.Depth },
            SrcSubresource = new ImageSubresourceLayers
            {
                LayerCount = 1,
                AspectMask = vkSource.ImageAspectMask
            },
            DstSubresource = new ImageSubresourceLayers
            {
                LayerCount = 1,
                AspectMask = vkDestination.ImageAspectMask
            }
        };

        _gd.Vk.CmdResolveImage(
            _cb,
            vkSource.OptimalDeviceImage,
            ImageLayout.TransferSrcOptimal,
            vkDestination.OptimalDeviceImage,
            ImageLayout.TransferDstOptimal,
            1,
            in region);

        sourceAccess.Restore(_cb, _currentStagingInfo.ImageLayouts);
        destinationAccess.Restore(_cb, _currentStagingInfo.ImageLayouts);
    }

    private protected override void EndCore()
    {
        if (!_commandBufferBegun)
        {
            throw new NeoVeldridException("CommandBuffer must have been started before End() may be called.");
        }

        _commandBufferBegun = false;
        _commandBufferEnded = true;

        FinalizeCurrentFramebuffer();

        Result result = _gd.Vk.EndCommandBuffer(_cb);
        CheckResult(result);
        lock (_commandBufferListLock)
        {
            _submittedCommandBuffers.Add(_cb);
        }
    }

    private protected override void SetFramebufferCore(Framebuffer fb)
    {
        FinalizeCurrentFramebuffer();

        VkFramebufferBase vkFB = Util.AssertSubtype<Framebuffer, VkFramebufferBase>(fb);
        _currentFramebuffer = vkFB;
        _currentFramebufferEverActive = false;
        _newFramebuffer = true;
        Util.EnsureArrayMinimumSize(ref _scissorRects, Math.Max(1, (uint)vkFB.ColorTargets.Count));
        uint clearValueCount = (uint)vkFB.ColorTargets.Count;
        Util.EnsureArrayMinimumSize(ref _clearValues, clearValueCount + 1); // Leave an extra space for the depth value (tracked separately).
        Util.ClearArray(_validColorClearValues);
        Util.EnsureArrayMinimumSize(ref _validColorClearValues, clearValueCount);
        _currentStagingInfo.Resources.Add(vkFB.RefCount);

        if (fb is VkSwapchainFramebuffer scFB)
        {
            _currentStagingInfo.Resources.Add(scFB.Swapchain.RefCount);
        }
    }

    private void EnsureRenderPassActive()
    {
        if (_activeRenderPass.Handle == default)
        {
            BeginCurrentRenderPass();
        }
    }

    private void EnsureNoRenderPass()
    {
        if (_activeRenderPass.Handle != default)
        {
            EndCurrentRenderPass();
        }
        else if (!_currentFramebufferEverActive && _currentFramebuffer != null)
        {
            // A clear is queued until the first render pass. Materialize that
            // pass before any transfer or compute command which follows it.
            BeginCurrentRenderPass();
            EndCurrentRenderPass();
        }
    }

    private void FinalizeCurrentFramebuffer()
    {
        if (_currentFramebuffer == null)
            return;

        EnsureNoRenderPass();
        _currentFramebuffer.TransitionToExternalLayouts(
            _cb,
            _currentStagingInfo.ImageLayouts);
    }

    private void BeginCurrentRenderPass()
    {
        Debug.Assert(_activeRenderPass.Handle == default);
        Debug.Assert(_currentFramebuffer != null);
        _currentFramebufferEverActive = true;

        uint attachmentCount = _currentFramebuffer.AttachmentCount;
        bool haveAnyAttachments = _currentFramebuffer.ColorTargets.Count > 0 || _currentFramebuffer.DepthTarget != null;
        bool haveAllClearValues = _depthClearValue.HasValue || _currentFramebuffer.DepthTarget == null;
        bool haveAnyClearValues = _depthClearValue.HasValue;
        for (int i = 0; i < _currentFramebuffer.ColorTargets.Count; i++)
        {
            if (!_validColorClearValues[i])
            {
                haveAllClearValues = false;
            }
            else
            {
                haveAnyClearValues = true;
            }
        }

        if (haveAnyAttachments && !_newFramebuffer)
        {
            SynchronizeFramebufferContinuation();
        }

        RenderPassBeginInfo renderPassBI = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(_currentFramebuffer.RenderableWidth, _currentFramebuffer.RenderableHeight)),
            Framebuffer = _currentFramebuffer.CurrentFramebuffer
        };

        if (!haveAnyAttachments || !haveAllClearValues)
        {
            renderPassBI.RenderPass = _newFramebuffer
                ? _currentFramebuffer.RenderPassNoClear_Init
                : _currentFramebuffer.RenderPassNoClear_Load;
            _currentFramebuffer.PrepareForRenderPass(
                _cb,
                _newFramebuffer
                    ? VkRenderPassInitialLayoutKind.FirstUse
                    : VkRenderPassInitialLayoutKind.Continuation,
                _currentStagingInfo.ImageLayouts);
            _gd.Vk.CmdBeginRenderPass(_cb, in renderPassBI, SubpassContents.Inline);
            _activeRenderPass = renderPassBI.RenderPass;

            if (haveAnyClearValues)
            {
                if (_depthClearValue.HasValue)
                {
                    ClearDepthStencilCore(_depthClearValue.Value.DepthStencil.Depth, (byte)_depthClearValue.Value.DepthStencil.Stencil);
                    _depthClearValue = null;
                }

                for (uint i = 0; i < _currentFramebuffer.ColorTargets.Count; i++)
                {
                    if (_validColorClearValues[i])
                    {
                        _validColorClearValues[i] = false;
                        ClearValue vkClearValue = _clearValues[i];
                        RgbaFloat clearColor = new RgbaFloat(
                            vkClearValue.Color.Float32_0,
                            vkClearValue.Color.Float32_1,
                            vkClearValue.Color.Float32_2,
                            vkClearValue.Color.Float32_3);
                        ClearColorTargetCore(i, clearColor);
                    }
                }
            }
        }
        else
        {
            // We have clear values for every attachment.
            renderPassBI.RenderPass = _currentFramebuffer.RenderPassClear;
            _currentFramebuffer.PrepareForRenderPass(
                _cb,
                VkRenderPassInitialLayoutKind.Discard,
                _currentStagingInfo.ImageLayouts);
            fixed (ClearValue* clearValuesPtr = &_clearValues[0])
            {
                renderPassBI.ClearValueCount = attachmentCount;
                renderPassBI.PClearValues = clearValuesPtr;
                if (_depthClearValue.HasValue)
                {
                    _clearValues[_currentFramebuffer.ColorTargets.Count] = _depthClearValue.Value;
                    _depthClearValue = null;
                }
                _gd.Vk.CmdBeginRenderPass(_cb, in renderPassBI, SubpassContents.Inline);
                _activeRenderPass = _currentFramebuffer.RenderPassClear;
                Util.ClearArray(_validColorClearValues);
            }
        }

        _newFramebuffer = false;
    }

    private void EndCurrentRenderPass()
    {
        Debug.Assert(_activeRenderPass.Handle != default);
        _gd.Vk.CmdEndRenderPass(_cb);
        _currentFramebuffer.RecordRenderPassFinalLayouts(
            _cb,
            _currentStagingInfo.ImageLayouts);
        _activeRenderPass = default;
    }

    private void SynchronizeFramebufferContinuation()
    {
        // A transfer or compute command can suspend the current render pass.
        // Make its attachment stores available and visible before the load
        // pass resumes the same framebuffer.
        MemoryBarrier attachmentStoreBarrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask =
                AccessFlags.ColorAttachmentWriteBit
                | AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask =
                AccessFlags.ColorAttachmentReadBit
                | AccessFlags.ColorAttachmentWriteBit
                | AccessFlags.DepthStencilAttachmentReadBit
                | AccessFlags.DepthStencilAttachmentWriteBit
        };
        const PipelineStageFlags attachmentStages =
            PipelineStageFlags.ColorAttachmentOutputBit
            | PipelineStageFlags.EarlyFragmentTestsBit
            | PipelineStageFlags.LateFragmentTestsBit;
        _gd.Vk.CmdPipelineBarrier(
            _cb,
            attachmentStages,
            attachmentStages,
            0,
            1,
            &attachmentStoreBarrier,
            0,
            null,
            0,
            null);
    }

    private protected override void SetVertexBufferCore(uint index, DeviceBuffer buffer, uint offset)
    {
        VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(buffer);
        VkBufferHandle deviceBuffer = vkBuffer.DeviceBuffer;
        ulong offset64 = offset;
        _gd.Vk.CmdBindVertexBuffers(_cb, index, 1, in deviceBuffer, in offset64);
        TrackBuffer(vkBuffer);
    }

    private void TrackBuffer(VkBuffer buffer)
    {
        _currentStagingInfo.Resources.Add(buffer.RefCount);
        _currentStagingInfo.SubmissionAccesses.Add(buffer.SubmissionAccess);
    }

    private void TrackTexture(VkTexture texture)
    {
        _currentStagingInfo.Resources.Add(texture.RefCount);
        if ((texture.Usage & TextureUsage.Staging) != 0)
            _currentStagingInfo.SubmissionAccesses.Add(texture.SubmissionAccess);
    }

    private void TrackTextureUploadDestination(VkTexture destination)
    {
        _currentStagingInfo.Resources.Add(destination.RefCount);

        ICommandListTextureUploadLifecycleObserver observer =
            _gd.CommandListTextureUploadLifecycleObserver;
        if (observer is null)
            return;

        StagingResourceInfo info = _currentStagingInfo;
        if (info.TextureUploadLifecycleObserver is not null
            && !ReferenceEquals(info.TextureUploadLifecycleObserver, observer))
        {
            throw new InvalidOperationException(
                "The texture-upload lifecycle observer cannot change during a Vulkan command-list recording.");
        }

        info.TextureUploadLifecycleObserver = observer;
        info.ObservedTextureUploadDestinations ??= new List<VkTexture>();
        if (!info.ObservedTextureUploadDestinations.Contains(destination))
            info.ObservedTextureUploadDestinations.Add(destination);
    }

    private void AcquireObservedTextureUploadRetentions(
        StagingResourceInfo info)
    {
        if (info.TextureUploadLifecycleObserver is null)
            return;

        Debug.Assert(info.AcquiredTextureUploadRetentionCount == 0);
        for (int i = 0; i < info.ObservedTextureUploadDestinations.Count; i++)
        {
            _gd.NotifyTextureUploadRetentionAcquired(
                info.TextureUploadLifecycleObserver,
                this,
                info.ObservedTextureUploadDestinations[i]);
            info.AcquiredTextureUploadRetentionCount++;
        }
    }

    private void ReleaseObservedTextureUploadRetentions(
        StagingResourceInfo info)
    {
        if (info.TextureUploadLifecycleObserver is null)
            return;

        ICommandListTextureUploadLifecycleObserver observer =
            info.TextureUploadLifecycleObserver;
        int releaseCount = info.AcquiredTextureUploadRetentionCount;
        info.AcquiredTextureUploadRetentionCount = 0;

        for (int i = 0; i < releaseCount; i++)
        {
            _gd.NotifyTextureUploadRetentionReleased(
                observer,
                this,
                info.ObservedTextureUploadDestinations[i]
                    .RefCount.CurrentCount);
        }
    }

    private protected override void SetIndexBufferCore(DeviceBuffer buffer, IndexFormat format, uint offset)
    {
        VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(buffer);
        _gd.Vk.CmdBindIndexBuffer(_cb, vkBuffer.DeviceBuffer, offset, VkFormats.VdToVkIndexFormat(format));
        TrackBuffer(vkBuffer);
    }

    private protected override void SetPipelineCore(Pipeline pipeline)
    {
        VkPipeline vkPipeline = Util.AssertSubtype<Pipeline, VkPipeline>(pipeline);
        if (!pipeline.IsComputePipeline && _currentGraphicsPipeline != pipeline)
        {
            Util.EnsureArrayMinimumSize(ref _currentGraphicsResourceSets, vkPipeline.ResourceSetCount);
            ClearSets(_currentGraphicsResourceSets);
            Util.EnsureArrayMinimumSize(ref _graphicsResourceSetsChanged, vkPipeline.ResourceSetCount);
            _gd.Vk.CmdBindPipeline(_cb, PipelineBindPoint.Graphics, vkPipeline.DevicePipeline);
            _currentGraphicsPipeline = vkPipeline;
        }
        else if (pipeline.IsComputePipeline && _currentComputePipeline != pipeline)
        {
            Util.EnsureArrayMinimumSize(ref _currentComputeResourceSets, vkPipeline.ResourceSetCount);
            ClearSets(_currentComputeResourceSets);
            Util.EnsureArrayMinimumSize(ref _computeResourceSetsChanged, vkPipeline.ResourceSetCount);
            _gd.Vk.CmdBindPipeline(_cb, PipelineBindPoint.Compute, vkPipeline.DevicePipeline);
            _currentComputePipeline = vkPipeline;
        }

        _currentStagingInfo.Resources.Add(vkPipeline.RefCount);
    }

    private void ClearSets(BoundResourceSetInfo[] boundSets)
    {
        foreach (BoundResourceSetInfo boundSetInfo in boundSets)
        {
            boundSetInfo.Offsets.Dispose();
        }
        Util.ClearArray(boundSets);
    }

    private protected override void SetGraphicsResourceSetCore(uint slot, ResourceSet rs, uint dynamicOffsetsCount, ref uint dynamicOffsets)
    {
        if (!_currentGraphicsResourceSets[slot].Equals(rs, dynamicOffsetsCount, ref dynamicOffsets))
        {
            _currentGraphicsResourceSets[slot].Offsets.Dispose();
            _currentGraphicsResourceSets[slot] = new BoundResourceSetInfo(rs, dynamicOffsetsCount, ref dynamicOffsets);
            _graphicsResourceSetsChanged[slot] = true;
            VkResourceSet vkRS = Util.AssertSubtype<ResourceSet, VkResourceSet>(rs);
        }
    }

    private protected override void SetComputeResourceSetCore(uint slot, ResourceSet rs, uint dynamicOffsetsCount, ref uint dynamicOffsets)
    {
        if (!_currentComputeResourceSets[slot].Equals(rs, dynamicOffsetsCount, ref dynamicOffsets))
        {
            _currentComputeResourceSets[slot].Offsets.Dispose();
            _currentComputeResourceSets[slot] = new BoundResourceSetInfo(rs, dynamicOffsetsCount, ref dynamicOffsets);
            _computeResourceSetsChanged[slot] = true;
            VkResourceSet vkRS = Util.AssertSubtype<ResourceSet, VkResourceSet>(rs);
        }
    }

    private protected override void SetScissorRectCore(
        uint index,
        uint x,
        uint y,
        uint width,
        uint height)
    {
        if (index == 0 || _gd.Features.MultipleViewports)
        {
            Rect2D scissor = new Rect2D(new Offset2D((int)x, (int)y), new Extent2D((uint)width, (uint)height));
            Rect2D current = _scissorRects[index];
            if (scissor.Offset.X != current.Offset.X ||
                scissor.Offset.Y != current.Offset.Y ||
                scissor.Extent.Width != current.Extent.Width ||
                scissor.Extent.Height != current.Extent.Height)
            {
                _scissorRects[index] = scissor;
                _gd.Vk.CmdSetScissor(_cb, index, 1, in scissor);
            }
        }
    }

    private protected override void SetViewportCore(
        uint index,
        ref Viewport viewport)
    {
        if (index == 0 || _gd.Features.MultipleViewports)
        {
            float vpY = _gd.IsClipSpaceYInverted
                ? viewport.Y
                : viewport.Height + viewport.Y;
            float vpHeight = _gd.IsClipSpaceYInverted
                ? viewport.Height
                : -viewport.Height;

            Silk.NET.Vulkan.Viewport vkViewport = new Silk.NET.Vulkan.Viewport
            {
                X = viewport.X,
                Y = vpY,
                Width = viewport.Width,
                Height = vpHeight,
                MinDepth = viewport.MinDepth,
                MaxDepth = viewport.MaxDepth
            };

            _gd.Vk.CmdSetViewport(_cb, index, 1, in vkViewport);
        }
    }

    private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
    {
        StagingBufferAllocation staging = AllocateStagingBuffer(sizeInBytes);
        _gd.UpdateBuffer(staging.Buffer, staging.Offset, source, sizeInBytes);
        CopyBuffer(staging.Buffer, staging.Offset, buffer, bufferOffsetInBytes, sizeInBytes);
    }

    private protected override void UpdateTextureCore(
        Texture texture,
        IntPtr source,
        uint sizeInBytes,
        uint x,
        uint y,
        uint z,
        uint width,
        uint height,
        uint depth,
        uint mipLevel,
        uint arrayLayer)
    {
        EnsureNoRenderPass();

        // Reuse the command list's submission-owned staging pages. The page is
        // retained until this exact command buffer completes, so later frames
        // cannot overwrite bytes which Vulkan is still reading.
        uint stagingAlignment =
            VkTextureUploadRecorder.GetRequiredStagingAlignment(
                texture.Format);
        StagingBufferAllocation staging = AllocateStagingBuffer(
            sizeInBytes,
            stagingAlignment);
        _gd.UpdateBuffer(
            staging.Buffer,
            staging.Offset,
            source,
            sizeInBytes);
        TrackBuffer(staging.Buffer);

        VkTexture destination = Util.AssertSubtype<Texture, VkTexture>(texture);
        TrackTextureUploadDestination(destination);
        VkTextureUploadRecorder.Record(
            _gd,
            _cb,
            staging.Buffer,
            staging.Offset,
            sizeInBytes,
            destination,
            x,
            y,
            z,
            width,
            height,
            depth,
            mipLevel,
            arrayLayer,
            _currentStagingInfo.ImageLayouts);
    }

    private protected override void CopyBufferCore(
        DeviceBuffer source,
        uint sourceOffset,
        DeviceBuffer destination,
        uint destinationOffset,
        uint sizeInBytes)
    {
        EnsureNoRenderPass();

        VkBuffer srcVkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(source);
        TrackBuffer(srcVkBuffer);
        VkBuffer dstVkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(destination);
        TrackBuffer(dstVkBuffer);

        VkBufferCopyRecorder.Record(
            _gd,
            _cb,
            srcVkBuffer,
            sourceOffset,
            dstVkBuffer,
            destinationOffset,
            sizeInBytes);
    }

    private protected override void CopyTextureCore(
        Texture source,
        uint srcX, uint srcY, uint srcZ,
        uint srcMipLevel,
        uint srcBaseArrayLayer,
        Texture destination,
        uint dstX, uint dstY, uint dstZ,
        uint dstMipLevel,
        uint dstBaseArrayLayer,
        uint width, uint height, uint depth,
        uint layerCount)
    {
        EnsureNoRenderPass();
        CopyTextureCore_VkCommandBuffer(
            _gd.Vk,
            _cb,
            source, srcX, srcY, srcZ, srcMipLevel, srcBaseArrayLayer,
            destination, dstX, dstY, dstZ, dstMipLevel, dstBaseArrayLayer,
            width, height, depth, layerCount,
            _currentStagingInfo.ImageLayouts);

        VkTexture srcVkTexture = Util.AssertSubtype<Texture, VkTexture>(source);
        TrackTexture(srcVkTexture);
        VkTexture dstVkTexture = Util.AssertSubtype<Texture, VkTexture>(destination);
        TrackTexture(dstVkTexture);
    }

    internal static void CopyTextureCore_VkCommandBuffer(
        VkApi vk,
        CommandBuffer cb,
        Texture source,
        uint srcX, uint srcY, uint srcZ,
        uint srcMipLevel,
        uint srcBaseArrayLayer,
        Texture destination,
        uint dstX, uint dstY, uint dstZ,
        uint dstMipLevel,
        uint dstBaseArrayLayer,
        uint width, uint height, uint depth,
        uint layerCount,
        VkImageLayoutTransaction transaction = null)
    {
        VkTexture srcVkTexture = Util.AssertSubtype<Texture, VkTexture>(source);
        VkTexture dstVkTexture = Util.AssertSubtype<Texture, VkTexture>(destination);

        bool sourceIsStaging = (source.Usage & TextureUsage.Staging) == TextureUsage.Staging;
        bool destIsStaging = (destination.Usage & TextureUsage.Staging) == TextureUsage.Staging;
        if ((sourceIsStaging || destIsStaging)
            && FormatHelpers.IsStencilFormat(source.Format))
        {
            throw new NeoVeldridException(
                "Vulkan staging copies do not define a packed depth-stencil plane layout. Use a depth-only format or an aspect-explicit transfer API.");
        }

        Util.ClampCompressedCopyExtentToMipEdges(
            source, srcX, srcY, srcMipLevel, !sourceIsStaging,
            destination, dstX, dstY, dstMipLevel, !destIsStaging,
            ref width, ref height);

        if (!sourceIsStaging && !destIsStaging)
        {
            ImageSubresourceLayers srcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = layerCount,
                MipLevel = srcMipLevel,
                BaseArrayLayer = srcBaseArrayLayer
            };

            ImageSubresourceLayers dstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = layerCount,
                MipLevel = dstMipLevel,
                BaseArrayLayer = dstBaseArrayLayer
            };

            ImageCopy region = new ImageCopy
            {
                SrcOffset = new Offset3D { X = (int)srcX, Y = (int)srcY, Z = (int)srcZ },
                DstOffset = new Offset3D { X = (int)dstX, Y = (int)dstY, Z = (int)dstZ },
                SrcSubresource = srcSubresource,
                DstSubresource = dstSubresource,
                Extent = new Extent3D { Width = width, Height = height, Depth = depth }
            };

            VkImageTransferLayout.Access sourceAccess =
                VkImageTransferLayout.PrepareForRead(
                cb,
                srcVkTexture,
                srcMipLevel,
                1,
                srcBaseArrayLayer,
                layerCount,
                transaction);

            VkImageTransferLayout.Access destinationAccess =
                VkImageTransferLayout.PrepareForWrite(
                vk,
                cb,
                dstVkTexture,
                dstMipLevel,
                1,
                dstBaseArrayLayer,
                layerCount,
                transaction);

            vk.CmdCopyImage(
                cb,
                srcVkTexture.OptimalDeviceImage,
                ImageLayout.TransferSrcOptimal,
                dstVkTexture.OptimalDeviceImage,
                ImageLayout.TransferDstOptimal,
                1,
                in region);

            sourceAccess.Restore(cb, transaction);
            destinationAccess.Restore(cb, transaction);
        }
        else if (sourceIsStaging && !destIsStaging)
        {
            VkBufferHandle srcBuffer = srcVkTexture.StagingBuffer;
            VkImageHandle dstImage = dstVkTexture.OptimalDeviceImage;
            VkImageTransferLayout.Access destinationAccess =
                VkImageTransferLayout.PrepareForWrite(
                vk,
                cb,
                dstVkTexture,
                dstMipLevel,
                1,
                dstBaseArrayLayer,
                layerCount,
                transaction);

            Util.GetMipDimensions(srcVkTexture, srcMipLevel, out uint mipWidth, out uint mipHeight, out _);
            uint blockSize = FormatHelpers.IsCompressedFormat(srcVkTexture.Format) ? 4u : 1u;
            uint bufferRowLength = AlignUp(mipWidth, blockSize);
            uint bufferImageHeight = AlignUp(mipHeight, blockSize);
            uint compressedX = srcX / blockSize;
            uint compressedY = srcY / blockSize;
            uint blockSizeInBytes = blockSize == 1
                ? FormatSizeHelpers.GetSizeInBytes(srcVkTexture.Format)
                : FormatHelpers.GetBlockSizeInBytes(srcVkTexture.Format);

            // A staging array layer contains its complete mip chain. Vulkan's
            // implicit buffer array-layer stride only accounts for this one
            // copy extent, so each layer needs its authoritative mip offset.
            var regions = stackalloc BufferImageCopy[checked((int)layerCount)];
            for (uint layer = 0; layer < layerCount; layer++)
            {
                SubresourceLayout srcLayout = srcVkTexture.GetSubresourceLayout(
                    srcVkTexture.CalculateSubresource(
                        srcMipLevel,
                        srcBaseArrayLayer + layer));
                ImageSubresourceLayers dstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = dstVkTexture.ImageAspectMask,
                    LayerCount = 1,
                    MipLevel = dstMipLevel,
                    BaseArrayLayer = dstBaseArrayLayer + layer
                };

                regions[layer] = new BufferImageCopy
                {
                    BufferOffset = srcLayout.Offset
                        + (srcZ * srcLayout.DepthPitch)
                        + (compressedY * srcLayout.RowPitch)
                        + (compressedX * blockSizeInBytes),
                    BufferRowLength = bufferRowLength,
                    BufferImageHeight = bufferImageHeight,
                    ImageExtent = new Extent3D { Width = width, Height = height, Depth = depth },
                    ImageOffset = new Offset3D { X = (int)dstX, Y = (int)dstY, Z = (int)dstZ },
                    ImageSubresource = dstSubresource
                };
            }

            VkBufferTransferAccess.BeginTransferRead(vk, cb, srcBuffer);
            vk.CmdCopyBufferToImage(
                cb,
                srcBuffer,
                dstImage,
                ImageLayout.TransferDstOptimal,
                layerCount,
                regions);
            VkBufferTransferAccess.EndTransferRead(vk, cb, srcBuffer);

            destinationAccess.Restore(cb, transaction);
        }
        else if (!sourceIsStaging && destIsStaging)
        {
            VkImageHandle srcImage = srcVkTexture.OptimalDeviceImage;
            VkImageTransferLayout.Access sourceAccess =
                VkImageTransferLayout.PrepareForRead(
                cb,
                srcVkTexture,
                srcMipLevel,
                1,
                srcBaseArrayLayer,
                layerCount,
                transaction);

            VkBufferHandle dstBuffer = dstVkTexture.StagingBuffer;

            ImageAspectFlags aspect = srcVkTexture.ImageAspectMask;

            Util.GetMipDimensions(dstVkTexture, dstMipLevel, out uint mipWidth, out uint mipHeight, out _);
            uint blockSize = FormatHelpers.IsCompressedFormat(srcVkTexture.Format) ? 4u : 1u;
            uint bufferRowLength = AlignUp(mipWidth, blockSize);
            uint bufferImageHeight = AlignUp(mipHeight, blockSize);
            uint compressedDstX = dstX / blockSize;
            uint compressedDstY = dstY / blockSize;
            uint blockSizeInBytes = blockSize == 1
                ? FormatSizeHelpers.GetSizeInBytes(dstVkTexture.Format)
                : FormatHelpers.GetBlockSizeInBytes(dstVkTexture.Format);

            var layers = stackalloc BufferImageCopy[checked((int)layerCount)];
            for(uint layer = 0; layer < layerCount; layer++)
            {
                SubresourceLayout dstLayout = dstVkTexture.GetSubresourceLayout(
                    dstVkTexture.CalculateSubresource(dstMipLevel, dstBaseArrayLayer + layer));

                ImageSubresourceLayers srcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = aspect,
                    LayerCount = 1,
                    MipLevel = srcMipLevel,
                    BaseArrayLayer = srcBaseArrayLayer + layer
                };

                BufferImageCopy region = new BufferImageCopy
                {
                    BufferRowLength = bufferRowLength,
                    BufferImageHeight = bufferImageHeight,
                    BufferOffset = dstLayout.Offset
                        + (dstZ * dstLayout.DepthPitch)
                        + (compressedDstY * dstLayout.RowPitch)
                        + (compressedDstX * blockSizeInBytes),
                    ImageExtent = new Extent3D { Width = width, Height = height, Depth = depth },
                    ImageOffset = new Offset3D { X = (int)srcX, Y = (int)srcY, Z = (int)srcZ },
                    ImageSubresource = srcSubresource
                };

                layers[layer] = region;
            }

            VkBufferTransferAccess.BeginTransferWrite(vk, cb, dstBuffer);
            vk.CmdCopyImageToBuffer(cb, srcImage, ImageLayout.TransferSrcOptimal, dstBuffer, layerCount, layers);
            VkBufferTransferAccess.EndTransferWrite(vk, cb, dstBuffer);

            sourceAccess.Restore(cb, transaction);
        }
        else
        {
            Debug.Assert(sourceIsStaging && destIsStaging);
            VkBufferHandle srcBuffer = srcVkTexture.StagingBuffer;
            VkBufferHandle dstBuffer = dstVkTexture.StagingBuffer;

            VkBufferTransferAccess.BeginTransferRead(vk, cb, srcBuffer);
            VkBufferTransferAccess.BeginTransferWrite(vk, cb, dstBuffer);

            // Array layers advance by the complete mip-chain pitch, while Z
            // slices advance by this mip's depth pitch. Resolve every layer's
            // subresource first, then walk its depth slices independently.
            for (uint layer = 0; layer < layerCount; layer++)
            {
                SubresourceLayout srcLayout = srcVkTexture.GetSubresourceLayout(
                    srcVkTexture.CalculateSubresource(
                        srcMipLevel,
                        srcBaseArrayLayer + layer));
                SubresourceLayout dstLayout = dstVkTexture.GetSubresourceLayout(
                    dstVkTexture.CalculateSubresource(
                        dstMipLevel,
                        dstBaseArrayLayer + layer));

                if (!FormatHelpers.IsCompressedFormat(source.Format))
                {
                    uint pixelSize = FormatSizeHelpers.GetSizeInBytes(srcVkTexture.Format);
                    for (uint zz = 0; zz < depth; zz++)
                    {
                        for (uint yy = 0; yy < height; yy++)
                        {
                            BufferCopy region = new BufferCopy
                            {
                                SrcOffset = srcLayout.Offset
                                    + srcLayout.DepthPitch * (zz + srcZ)
                                    + srcLayout.RowPitch * (yy + srcY)
                                    + pixelSize * srcX,
                                DstOffset = dstLayout.Offset
                                    + dstLayout.DepthPitch * (zz + dstZ)
                                    + dstLayout.RowPitch * (yy + dstY)
                                    + pixelSize * dstX,
                                Size = width * pixelSize,
                            };

                            vk.CmdCopyBuffer(cb, srcBuffer, dstBuffer, 1, in region);
                        }
                    }
                }
                else // IsCompressedFormat
                {
                    uint denseRowSize = FormatHelpers.GetRowPitch(width, source.Format);
                    uint numRows = FormatHelpers.GetNumRows(height, source.Format);
                    uint compressedSrcX = srcX / 4;
                    uint compressedSrcY = srcY / 4;
                    uint compressedDstX = dstX / 4;
                    uint compressedDstY = dstY / 4;
                    uint blockSizeInBytes = FormatHelpers.GetBlockSizeInBytes(source.Format);

                    for (uint zz = 0; zz < depth; zz++)
                    {
                        for (uint row = 0; row < numRows; row++)
                        {
                            BufferCopy region = new BufferCopy
                            {
                                SrcOffset = srcLayout.Offset
                                    + srcLayout.DepthPitch * (zz + srcZ)
                                    + srcLayout.RowPitch * (row + compressedSrcY)
                                    + blockSizeInBytes * compressedSrcX,
                                DstOffset = dstLayout.Offset
                                    + dstLayout.DepthPitch * (zz + dstZ)
                                    + dstLayout.RowPitch * (row + compressedDstY)
                                    + blockSizeInBytes * compressedDstX,
                                Size = denseRowSize,
                            };

                            vk.CmdCopyBuffer(cb, srcBuffer, dstBuffer, 1, in region);
                        }
                    }
                }
            }

            VkBufferTransferAccess.EndTransferRead(vk, cb, srcBuffer);
            VkBufferTransferAccess.EndTransferWrite(vk, cb, dstBuffer);
        }
    }

    private protected override void GenerateMipmapsCore(Texture texture)
    {
        EnsureNoRenderPass();
        VkTexture vkTex = Util.AssertSubtype<Texture, VkTexture>(texture);
        _currentStagingInfo.Resources.Add(vkTex.RefCount);

        uint layerCount = vkTex.ArrayLayers;
        if ((vkTex.Usage & TextureUsage.Cubemap) != 0)
        {
            layerCount *= 6;
        }

        ImageBlit region;

        uint width = vkTex.Width;
        uint height = vkTex.Height;
        uint depth = vkTex.Depth;
        for (uint level = 1; level < vkTex.MipLevels; level++)
        {
            vkTex.TransitionImageLayoutNonmatching(
                _cb,
                level - 1,
                1,
                0,
                layerCount,
                ImageLayout.TransferSrcOptimal,
                _currentStagingInfo.ImageLayouts);
            vkTex.TransitionImageLayoutNonmatching(
                _cb,
                level,
                1,
                0,
                layerCount,
                ImageLayout.TransferDstOptimal,
                _currentStagingInfo.ImageLayouts);

            VkImageHandle deviceImage = vkTex.OptimalDeviceImage;
            uint mipWidth = Math.Max(width >> 1, 1);
            uint mipHeight = Math.Max(height >> 1, 1);
            uint mipDepth = Math.Max(depth >> 1, 1);

            region.SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseArrayLayer = 0,
                LayerCount = layerCount,
                MipLevel = level - 1
            };
            region.SrcOffsets = default;
            region.SrcOffsets.Element0 = new Offset3D();
            region.SrcOffsets.Element1 = new Offset3D { X = (int)width, Y = (int)height, Z = (int)depth };
            region.DstOffsets = default;
            region.DstOffsets.Element0 = new Offset3D();

            region.DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseArrayLayer = 0,
                LayerCount = layerCount,
                MipLevel = level
            };

            region.DstOffsets.Element1 = new Offset3D { X = (int)mipWidth, Y = (int)mipHeight, Z = (int)mipDepth };
            _gd.Vk.CmdBlitImage(
                _cb,
                deviceImage, ImageLayout.TransferSrcOptimal,
                deviceImage, ImageLayout.TransferDstOptimal,
                1, &region,
                _gd.GetFormatFilter(vkTex.VkFormat));

            width = mipWidth;
            height = mipHeight;
            depth = mipDepth;
        }

        if ((vkTex.Usage & TextureUsage.Sampled) != 0)
        {
            vkTex.TransitionImageLayoutNonmatching(
                _cb,
                0,
                vkTex.MipLevels,
                0,
                layerCount,
                ImageLayout.ShaderReadOnlyOptimal,
                _currentStagingInfo.ImageLayouts);
        }
    }

    [Conditional("DEBUG")]
    private void DebugFullPipelineBarrier()
    {
        MemoryBarrier memoryBarrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.IndirectCommandReadBit |
                   AccessFlags.IndexReadBit |
                   AccessFlags.VertexAttributeReadBit |
                   AccessFlags.UniformReadBit |
                   AccessFlags.InputAttachmentReadBit |
                   AccessFlags.ShaderReadBit |
                   AccessFlags.ShaderWriteBit |
                   AccessFlags.ColorAttachmentReadBit |
                   AccessFlags.ColorAttachmentWriteBit |
                   AccessFlags.DepthStencilAttachmentReadBit |
                   AccessFlags.DepthStencilAttachmentWriteBit |
                   AccessFlags.TransferReadBit |
                   AccessFlags.TransferWriteBit |
                   AccessFlags.HostReadBit |
                   AccessFlags.HostWriteBit,
            DstAccessMask = AccessFlags.IndirectCommandReadBit |
                   AccessFlags.IndexReadBit |
                   AccessFlags.VertexAttributeReadBit |
                   AccessFlags.UniformReadBit |
                   AccessFlags.InputAttachmentReadBit |
                   AccessFlags.ShaderReadBit |
                   AccessFlags.ShaderWriteBit |
                   AccessFlags.ColorAttachmentReadBit |
                   AccessFlags.ColorAttachmentWriteBit |
                   AccessFlags.DepthStencilAttachmentReadBit |
                   AccessFlags.DepthStencilAttachmentWriteBit |
                   AccessFlags.TransferReadBit |
                   AccessFlags.TransferWriteBit |
                   AccessFlags.HostReadBit |
                   AccessFlags.HostWriteBit
        };

        _gd.Vk.CmdPipelineBarrier(
            _cb,
            PipelineStageFlags.AllCommandsBit, // srcStageMask
            PipelineStageFlags.AllCommandsBit, // dstStageMask
            0,
            1,                                  // memoryBarrierCount
            &memoryBarrier,                     // pMemoryBarriers
            0, null,
            0, null);
    }

    public override string Name
    {
        get => _name;
        set
        {
            _name = value;
            _gd.SetResourceName(this, value);
        }
    }

    private StagingBufferAllocation AllocateStagingBuffer(
        uint size,
        uint alignment = BufferCopyAlignment)
    {
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (alignment == 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));

        lock (_stagingLock)
        {
            uint offset = AlignUp(
                _currentStagingInfo.CurrentUploadOffset,
                alignment);
            VkBuffer current = _currentStagingInfo.CurrentUploadBuffer;
            if (current != null &&
                offset <= current.SizeInBytes &&
                size <= current.SizeInBytes - offset)
            {
                _currentStagingInfo.CurrentUploadOffset = checked(offset + size);
                return new StagingBufferAllocation(current, offset);
            }

            // A caller-provisioned page is already a retained capacity
            // decision. Requiring it to meet the lazy-allocation default
            // defeats small, exact preallocation and leaves that page idle
            // while allocating a second, much larger page on first use.
            uint requiredCapacity = AlignUp(size, alignment);
            VkBuffer ret = null;
            foreach (VkBuffer buffer in _availableStagingBuffers)
            {
                if (buffer.SizeInBytes >= requiredCapacity)
                {
                    ret = buffer;
                    _availableStagingBuffers.Remove(buffer);
                    break;
                }
            }
            if (ret == null)
            {
                uint createdCapacity = Math.Max(
                    Math.Max(
                        DefaultStagingUploadPageSize,
                        _initialStagingUploadPageSize),
                    requiredCapacity);
                ret = (VkBuffer)_gd.ResourceFactory.CreateBuffer(
                    new BufferDescription(createdCapacity, BufferUsage.Staging));
                ret.Name = $"Upload Page (CommandList {_name})";
            }

            _currentStagingInfo.BuffersUsed.Add(ret);
            _currentStagingInfo.CurrentUploadBuffer = ret;
            _currentStagingInfo.CurrentUploadOffset = size;
            return new StagingBufferAllocation(ret, 0u);
        }
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        uint remainder = value % alignment;
        return remainder == 0u
            ? value
            : checked(value + alignment - remainder);
    }

    private readonly struct StagingBufferAllocation
    {
        public VkBuffer Buffer { get; }
        public uint Offset { get; }

        public StagingBufferAllocation(VkBuffer buffer, uint offset)
        {
            Buffer = buffer;
            Offset = offset;
        }
    }

    private protected override void PushDebugGroupCore(string name)
    {
        vkCmdDebugMarkerBeginEXT_t func = _gd.MarkerBegin;
        if (func == null) { return; }

        DebugMarkerMarkerInfoEXT markerInfo = new DebugMarkerMarkerInfoEXT { SType = StructureType.DebugMarkerMarkerInfoExt };

        int byteCount = Encoding.UTF8.GetByteCount(name);
        byte* utf8Ptr = stackalloc byte[byteCount + 1];
        fixed (char* namePtr = name)
        {
            Encoding.UTF8.GetBytes(namePtr, name.Length, utf8Ptr, byteCount);
        }
        utf8Ptr[byteCount] = 0;

        markerInfo.PMarkerName = utf8Ptr;

        func(_cb, &markerInfo);
    }

    private protected override void PopDebugGroupCore()
    {
        vkCmdDebugMarkerEndEXT_t func = _gd.MarkerEnd;
        if (func == null) { return; }

        func(_cb);
    }

    private protected override void InsertDebugMarkerCore(string name)
    {
        vkCmdDebugMarkerInsertEXT_t func = _gd.MarkerInsert;
        if (func == null) { return; }

        DebugMarkerMarkerInfoEXT markerInfo = new DebugMarkerMarkerInfoEXT { SType = StructureType.DebugMarkerMarkerInfoExt };

        int byteCount = Encoding.UTF8.GetByteCount(name);
        byte* utf8Ptr = stackalloc byte[byteCount + 1];
        fixed (char* namePtr = name)
        {
            Encoding.UTF8.GetBytes(namePtr, name.Length, utf8Ptr, byteCount);
        }
        utf8Ptr[byteCount] = 0;

        markerInfo.PMarkerName = utf8Ptr;

        func(_cb, &markerInfo);
    }

    public override void Dispose()
    {
        RefCount.Decrement();
    }

    private void DisposeCore()
    {
        if (!_destroyed)
        {
            _currentStagingInfo?.ImageLayouts.Rollback();
            _destroyed = true;
            _gd.Vk.DestroyCommandPool(_gd.Device, _pool, null);

            lock (_stagingLock)
            {
                Debug.Assert(_submittedStagingInfos.Count == 0);

                foreach (VkBuffer buffer in _availableStagingBuffers)
                    buffer.Dispose();

                if (_currentStagingInfo != null)
                {
                    foreach (VkBuffer buffer in _currentStagingInfo.BuffersUsed)
                        buffer.Dispose();
                }
            }
        }
    }

    internal StagingResourcePoolSnapshot CaptureStagingResourcePoolSnapshot()
    {
        lock (_stagingLock)
        {
            int disposedBufferCount = 0;
            foreach (VkBuffer buffer in _availableStagingBuffers)
            {
                if (buffer.IsDisposed)
                    disposedBufferCount++;
            }

            return new StagingResourcePoolSnapshot(
                _availableStagingBuffers.Count,
                disposedBufferCount,
                _currentStagingInfo?.BuffersUsed.Count ?? 0);
        }
    }

    internal readonly record struct StagingResourcePoolSnapshot(
        int AvailableBufferCount,
        int DisposedAvailableBufferCount,
        int CurrentBufferCount);

    private class StagingResourceInfo
    {
        public uint Slot { get; }
        public List<VkBuffer> BuffersUsed { get; }
        public HashSet<ResourceRefCount> Resources { get; }
        public HashSet<VkMappableResourceSubmissionAccess> SubmissionAccesses { get; }
        public List<VkMappableResourceSubmissionAccess> AcquiredSubmissionAccesses { get; }
        public VkBuffer CurrentUploadBuffer { get; set; }
        public uint CurrentUploadOffset { get; set; }
        public bool SubmissionReferencesAcquired { get; set; }
        public List<VkTexture> ObservedTextureUploadDestinations { get; set; }
        public ICommandListTextureUploadLifecycleObserver
            TextureUploadLifecycleObserver { get; set; }
        public int AcquiredTextureUploadRetentionCount { get; set; }
        public VkImageLayoutTransaction ImageLayouts { get; } =
            new VkImageLayoutTransaction();

        public StagingResourceInfo(
            int initialTrackedResourceCapacity,
            uint slot)
        {
            Slot = slot;
            BuffersUsed = new List<VkBuffer>(RetainedStagingBufferCapacity);
            Resources = new HashSet<ResourceRefCount>(
                initialTrackedResourceCapacity,
                ReferenceEqualityComparer.Instance);
            SubmissionAccesses = new HashSet<VkMappableResourceSubmissionAccess>(
                initialTrackedResourceCapacity,
                ReferenceEqualityComparer.Instance);
            AcquiredSubmissionAccesses =
                new List<VkMappableResourceSubmissionAccess>(initialTrackedResourceCapacity);
        }

        public void Clear()
        {
            ImageLayouts.ResetForReuse();
            BuffersUsed.Clear();
            Resources.Clear();
            SubmissionAccesses.Clear();
            AcquiredSubmissionAccesses.Clear();
            CurrentUploadBuffer = null;
            CurrentUploadOffset = 0u;
            SubmissionReferencesAcquired = false;
            ObservedTextureUploadDestinations?.Clear();
            TextureUploadLifecycleObserver = null;
            AcquiredTextureUploadRetentionCount = 0;
        }
    }

    private StagingResourceInfo GetStagingResourceInfo()
    {
        if (TryTakeAvailableStagingResourceInfo() is { } available)
            return available;

        // Submission polling normally runs after Begin. Poll once here so a
        // completed record is reclaimed before deciding that the pool reached
        // its bound.
        _gd.ReclaimCompletedSubmissions();
        if (TryTakeAvailableStagingResourceInfo() is { } reclaimed)
            return reclaimed;

        if (_maximumInFlightSubmissionCount == 0)
            return new StagingResourceInfo(
                _initialTrackedResourceCapacityPerSubmission,
                0u);

        // All bounded records are in flight. Wait for this command list's
        // oldest submission rather than draining unrelated queues or growing
        // managed state and input latency without bound. Do not wait while
        // holding _stagingLock: completion recycles under the same lock.
        _gd.WaitForOldestSubmissionCompletion(this);

        return TryTakeAvailableStagingResourceInfo() ??
            throw new NeoVeldridException(
                "The Vulkan command-list staging pool remained exhausted after its oldest submission completed.");
    }

    private StagingResourceInfo TryTakeAvailableStagingResourceInfo()
    {
        lock (_stagingLock)
        {
            int availableCount = _availableStagingInfos.Count;
            if (availableCount == 0)
                return null;

            StagingResourceInfo ret = _availableStagingInfos[availableCount - 1];
            _availableStagingInfos.RemoveAt(availableCount - 1);
            return ret;
        }
    }

    private void RecycleStagingInfo(StagingResourceInfo info)
    {
        bool releaseCommandListReference =
            ReleaseSubmissionResourceReferences(info);

        lock (_stagingLock)
        {
            foreach (VkBuffer buffer in info.BuffersUsed)
            {
                _availableStagingBuffers.Add(buffer);
            }

            info.Clear();

            _availableStagingInfos.Add(info);
        }

        if (releaseCommandListReference)
            RefCount.Decrement();
    }
}
