using Silk.NET.Vulkan;
using static NeoVeldrid.Vk.VulkanUtil;
using System.Diagnostics;
using System;
using System.Collections.Generic;

namespace NeoVeldrid.Vk;

internal unsafe class VkTexture : Texture
{
    private readonly VkGraphicsDevice _gd;
    private Image _optimalImage;
    private VkMemoryBlock _memoryBlock;
    private Silk.NET.Vulkan.Buffer _stagingBuffer;
    private readonly uint _stagingBufferCapacity;
    private PixelFormat _format; // Static for regular images -- may change for shared staging images
    private readonly uint _actualImageArrayLayers;
    private bool _destroyed;

    // Immutable except for shared staging Textures.
    private uint _width;
    private uint _height;
    private uint _depth;
    private TextureStagingLayout _stagingLayout;

    public override uint Width => _width;

    public override uint Height => _height;

    public override uint Depth => _depth;

    public override PixelFormat Format => _format;

    public override uint MipLevels { get; }

    public override uint ArrayLayers { get; }
    public uint ActualArrayLayers => _actualImageArrayLayers;

    public override TextureUsage Usage { get; }

    public override TextureType Type { get; }

    public override TextureSampleCount SampleCount { get; }

    public override bool IsDisposed => _destroyed;

    public Image OptimalDeviceImage => _optimalImage;
    public Silk.NET.Vulkan.Buffer StagingBuffer => _stagingBuffer;
    internal uint StagingBufferCapacity => _stagingBufferCapacity;
    public VkMappableResourceSubmissionAccess SubmissionAccess { get; } =
        new VkMappableResourceSubmissionAccess();
    public VkMemoryBlock Memory => _memoryBlock;

    public Format VkFormat { get; }
    public SampleCountFlags VkSampleCount { get; }

    private ImageLayout[] _imageLayouts;
    private uint[] _imageLayoutRevisions;
    private readonly List<VkImageLayoutTransaction> _pendingLayoutTransactions =
        new List<VkImageLayoutTransaction>();
    private bool _isSwapchainTexture;
    private string _name;

    public ResourceRefCount RefCount { get; }
    public bool IsSwapchainTexture => _isSwapchainTexture;

    internal VkTexture(VkGraphicsDevice gd, ref TextureDescription description)
    {
        _gd = gd;
        _width = description.Width;
        _height = description.Height;
        _depth = description.Depth;
        MipLevels = description.MipLevels;
        ArrayLayers = description.ArrayLayers;
        bool isCubemap = ((description.Usage) & TextureUsage.Cubemap) == TextureUsage.Cubemap;
        _actualImageArrayLayers = isCubemap
            ? 6 * ArrayLayers
            : ArrayLayers;
        _format = description.Format;
        Usage = description.Usage;
        Type = description.Type;
        SampleCount = description.SampleCount;
        VkSampleCount = VkFormats.VdToVkSampleCount(SampleCount);
        VkFormat = VkFormats.VdToVkPixelFormat(Format, (description.Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil);

        bool isStaging = (Usage & TextureUsage.Staging) == TextureUsage.Staging;

        if (!isStaging)
        {
            ImageCreateInfo imageCI = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            imageCI.MipLevels = MipLevels;
            imageCI.ArrayLayers = _actualImageArrayLayers;
            imageCI.ImageType = VkFormats.VdToVkTextureType(Type);
            imageCI.Extent.Width = Width;
            imageCI.Extent.Height = Height;
            imageCI.Extent.Depth = Depth;
            imageCI.InitialLayout = ImageLayout.Undefined;
            imageCI.Usage = VkFormats.VdToVkTextureUsage(Usage);
            imageCI.Tiling = isStaging ? ImageTiling.Linear : ImageTiling.Optimal;
            imageCI.Format = VkFormat;
            imageCI.Flags = ImageCreateFlags.CreateMutableFormatBit;

            imageCI.Samples = VkSampleCount;
            if (isCubemap)
            {
                imageCI.Flags |= ImageCreateFlags.CreateCubeCompatibleBit;
            }

            uint subresourceCount = MipLevels * _actualImageArrayLayers * Depth;
            Result result = _gd.Vk.CreateImage(gd.Device, in imageCI, null, out _optimalImage);
            CheckResult(result);

            MemoryRequirements memoryRequirements;
            bool prefersDedicatedAllocation;
            if (_gd.GetImageMemoryRequirements2 != null)
            {
                ImageMemoryRequirementsInfo2KHR memReqsInfo2 = new ImageMemoryRequirementsInfo2KHR { SType = StructureType.ImageMemoryRequirementsInfo2Khr };
                memReqsInfo2.Image = _optimalImage;
                MemoryRequirements2KHR memReqs2 = new MemoryRequirements2KHR { SType = StructureType.MemoryRequirements2Khr };
                MemoryDedicatedRequirementsKHR dedicatedReqs = new MemoryDedicatedRequirementsKHR { SType = StructureType.MemoryDedicatedRequirementsKhr };
                memReqs2.PNext = &dedicatedReqs;
                _gd.GetImageMemoryRequirements2(_gd.Device, &memReqsInfo2, &memReqs2);
                memoryRequirements = memReqs2.MemoryRequirements;
                prefersDedicatedAllocation = dedicatedReqs.PrefersDedicatedAllocation || dedicatedReqs.RequiresDedicatedAllocation;
            }
            else
            {
                _gd.Vk.GetImageMemoryRequirements(gd.Device, _optimalImage, out memoryRequirements);
                prefersDedicatedAllocation = false;
            }

            VkMemoryBlock memoryToken = gd.MemoryManager.Allocate(
                gd.PhysicalDeviceMemProperties,
                memoryRequirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit,
                false,
                memoryRequirements.Size,
                memoryRequirements.Alignment,
                prefersDedicatedAllocation,
                _optimalImage,
                default);
            _memoryBlock = memoryToken;
            result = _gd.Vk.BindImageMemory(gd.Device, _optimalImage, _memoryBlock.DeviceMemory, _memoryBlock.Offset);
            CheckResult(result);

            _imageLayouts = new ImageLayout[subresourceCount];
            _imageLayoutRevisions = new uint[subresourceCount];
            for (int i = 0; i < _imageLayouts.Length; i++)
            {
                _imageLayouts[i] = ImageLayout.Undefined;
            }
        }
        else // isStaging
        {
            _stagingLayout = TextureStagingLayout.Create(description);
            _stagingBufferCapacity = _stagingLayout.TotalSizeInBytes;

            BufferCreateInfo bufferCI = new BufferCreateInfo { SType = StructureType.BufferCreateInfo };
            bufferCI.Usage = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;
            bufferCI.Size = _stagingBufferCapacity;
            Result result = _gd.Vk.CreateBuffer(_gd.Device, in bufferCI, null, out _stagingBuffer);
            CheckResult(result);

            MemoryRequirements bufferMemReqs;
            bool prefersDedicatedAllocation;
            if (_gd.GetBufferMemoryRequirements2 != null)
            {
                BufferMemoryRequirementsInfo2KHR memReqInfo2 = new BufferMemoryRequirementsInfo2KHR { SType = StructureType.BufferMemoryRequirementsInfo2Khr };
                memReqInfo2.Buffer = _stagingBuffer;
                MemoryRequirements2KHR memReqs2 = new MemoryRequirements2KHR { SType = StructureType.MemoryRequirements2Khr };
                MemoryDedicatedRequirementsKHR dedicatedReqs = new MemoryDedicatedRequirementsKHR { SType = StructureType.MemoryDedicatedRequirementsKhr };
                memReqs2.PNext = &dedicatedReqs;
                _gd.GetBufferMemoryRequirements2(_gd.Device, &memReqInfo2, &memReqs2);
                bufferMemReqs = memReqs2.MemoryRequirements;
                prefersDedicatedAllocation = dedicatedReqs.PrefersDedicatedAllocation || dedicatedReqs.RequiresDedicatedAllocation;
            }
            else
            {
                _gd.Vk.GetBufferMemoryRequirements(gd.Device, _stagingBuffer, out bufferMemReqs);
                prefersDedicatedAllocation = false;
            }

            // Use "host cached" memory when available, for better performance of GPU -> CPU transfers
            var propertyFlags = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit;
            if (!TryFindMemoryType(_gd.PhysicalDeviceMemProperties, bufferMemReqs.MemoryTypeBits, propertyFlags, out _))
            {
                propertyFlags ^= MemoryPropertyFlags.HostCachedBit;
            }
            _memoryBlock = _gd.MemoryManager.Allocate(
                _gd.PhysicalDeviceMemProperties,
                bufferMemReqs.MemoryTypeBits,
                propertyFlags,
                true,
                bufferMemReqs.Size,
                bufferMemReqs.Alignment,
                prefersDedicatedAllocation,
                default,
                _stagingBuffer);

            result = _gd.Vk.BindBufferMemory(_gd.Device, _stagingBuffer, _memoryBlock.DeviceMemory, _memoryBlock.Offset);
            CheckResult(result);
        }

        RefCount = new ResourceRefCount(RefCountedDispose);
        ClearIfRenderTarget();
        TransitionIfSampled();
    }

    // Used to construct Swapchain textures.
    internal VkTexture(
        VkGraphicsDevice gd,
        uint width,
        uint height,
        uint mipLevels,
        uint arrayLayers,
        Format vkFormat,
        TextureUsage usage,
        TextureSampleCount sampleCount,
        Image existingImage)
    {
        Debug.Assert(width > 0 && height > 0);
        _gd = gd;
        MipLevels = mipLevels;
        _width = width;
        _height = height;
        _depth = 1;
        VkFormat = vkFormat;
        _format = VkFormats.VkToVdPixelFormat(VkFormat);
        ArrayLayers = arrayLayers;
        Usage = usage;
        Type = TextureType.Texture2D;
        SampleCount = sampleCount;
        VkSampleCount = VkFormats.VdToVkSampleCount(sampleCount);
        _optimalImage = existingImage;
        _imageLayouts = new[] { ImageLayout.Undefined };
        _imageLayoutRevisions = new uint[1];
        _isSwapchainTexture = true;

        RefCount = new ResourceRefCount(RefCountedDispose);
        ClearIfRenderTarget();
    }

    private void ClearIfRenderTarget()
    {
        // If the image is going to be used as a render target, we need to clear the data before its first use.
        if ((Usage & TextureUsage.RenderTarget) != 0)
        {
            _gd.ClearColorTexture(this, new ClearColorValue(0, 0, 0, 0));
        }
        else if ((Usage & TextureUsage.DepthStencil) != 0)
        {
            _gd.ClearDepthTexture(this, new ClearDepthStencilValue(0, 0));
        }
    }

    private void TransitionIfSampled()
    {
        if ((Usage & TextureUsage.Sampled) != 0)
        {
            _gd.TransitionImageLayout(this, ImageLayout.ShaderReadOnlyOptimal);
        }
    }

    internal SubresourceLayout GetSubresourceLayout(uint subresource)
    {
        bool staging = _stagingBuffer.Handle != 0;
        Util.GetMipLevelAndArrayLayer(this, subresource, out uint mipLevel, out uint arrayLayer);
        if (!staging)
        {
            ImageAspectFlags aspect = (Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil
              ? (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)
              : ImageAspectFlags.ColorBit;
            ImageSubresource imageSubresource = new ImageSubresource
            {
                ArrayLayer = arrayLayer,
                MipLevel = mipLevel,
                AspectMask = aspect,
            };

            _gd.Vk.GetImageSubresourceLayout(_gd.Device, _optimalImage, in imageSubresource, out SubresourceLayout layout);
            return layout;
        }
        else
        {
            StagingTextureSubresourceLayout stagingLayout =
                _stagingLayout.GetSubresourceLayout(mipLevel, arrayLayer);

            SubresourceLayout layout = new SubresourceLayout()
            {
                Offset = stagingLayout.Offset,
                RowPitch = stagingLayout.RowPitch,
                DepthPitch = stagingLayout.DepthPitch,
                ArrayPitch = stagingLayout.ArrayPitch,
                Size = stagingLayout.SizeInBytes,
            };

            return layout;
        }
    }

    internal void TransitionImageLayout(
        CommandBuffer cb,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        ImageLayout newLayout,
        VkImageLayoutTransaction transaction = null)
    {
        if (_stagingBuffer.Handle != 0)
        {
            return;
        }

        lock (VkImageLayoutTransaction.SyncRoot)
        {
            EnsureLayoutAccessAllowedLocked(transaction);
            ObserveLayoutRangeLocked(
                transaction,
                baseMipLevel,
                levelCount,
                baseArrayLayer,
                layerCount);

            ImageLayout oldLayout = _imageLayouts[CalculateSubresource(baseMipLevel, baseArrayLayer)];
#if DEBUG
            for (uint level = 0; level < levelCount; level++)
            {
                for (uint layer = 0; layer < layerCount; layer++)
                {
                    if (_imageLayouts[CalculateSubresource(baseMipLevel + level, baseArrayLayer + layer)] != oldLayout)
                    {
                        throw new NeoVeldridException("Unexpected image layout.");
                    }
                }
            }
#endif
            if (oldLayout != newLayout)
            {
                ImageAspectFlags aspectMask = ImageAspectMask;
                VulkanUtil.TransitionImageLayout(
                    _gd.Vk,
                    cb,
                    OptimalDeviceImage,
                    baseMipLevel,
                    levelCount,
                    baseArrayLayer,
                    layerCount,
                    aspectMask,
                    oldLayout,
                    newLayout);

                for (uint level = 0; level < levelCount; level++)
                {
                    for (uint layer = 0; layer < layerCount; layer++)
                    {
                        SetImageLayoutStateLocked(
                            CalculateSubresource(baseMipLevel + level, baseArrayLayer + layer),
                            newLayout);
                    }
                }
            }
        }
    }

    internal void TransitionImageLayoutNonmatching(
        CommandBuffer cb,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        ImageLayout newLayout,
        VkImageLayoutTransaction transaction = null)
    {
        if (_stagingBuffer.Handle != 0)
        {
            return;
        }

        lock (VkImageLayoutTransaction.SyncRoot)
        {
            EnsureLayoutAccessAllowedLocked(transaction);
            ObserveLayoutRangeLocked(
                transaction,
                baseMipLevel,
                levelCount,
                baseArrayLayer,
                layerCount);

            for (uint level = baseMipLevel; level < baseMipLevel + levelCount; level++)
            {
                for (uint layer = baseArrayLayer; layer < baseArrayLayer + layerCount; layer++)
                {
                    uint subresource = CalculateSubresource(level, layer);
                    ImageLayout oldLayout = _imageLayouts[subresource];

                    if (oldLayout != newLayout)
                    {
                        VulkanUtil.TransitionImageLayout(
                            _gd.Vk,
                            cb,
                            OptimalDeviceImage,
                            level,
                            1,
                            layer,
                            1,
                            ImageAspectMask,
                            oldLayout,
                            newLayout);

                        SetImageLayoutStateLocked(subresource, newLayout);
                    }
                }
            }
        }
    }

    internal ImageLayout GetImageLayout(
        uint mipLevel,
        uint arrayLayer,
        VkImageLayoutTransaction transaction = null)
    {
        lock (VkImageLayoutTransaction.SyncRoot)
        {
            uint subresource = CalculateSubresource(mipLevel, arrayLayer);
            if (transaction != null)
            {
                transaction.ObserveLocked(
                    this,
                    subresource,
                    _imageLayouts[subresource],
                    _imageLayoutRevisions[subresource]);
            }

            return _imageLayouts[subresource];
        }
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

    internal void SetStagingDimensions(uint width, uint height, uint depth, PixelFormat format)
    {
        Debug.Assert(_stagingBuffer.Handle != 0);
        Debug.Assert(Usage == TextureUsage.Staging);
        TextureStagingLayout stagingLayout = TextureStagingLayout.Create(
            width,
            height,
            depth,
            MipLevels,
            ArrayLayers,
            format);
        if (stagingLayout.TotalSizeInBytes > _stagingBufferCapacity)
        {
            throw new NeoVeldridException(
                "The shared Vulkan staging texture is too small for the requested layout.");
        }

        _width = width;
        _height = height;
        _depth = depth;
        _format = format;
        _stagingLayout = stagingLayout;
    }

    private protected override void DisposeCore()
    {
        RefCount.Decrement();
    }

    private void RefCountedDispose()
    {
        if (_destroyed)
            return;

        // The cached view is a native child of the texture image. Do not
        // advance to image retirement unless that child has been released.
        // Texture tracks this stage independently, so a failed later stage can
        // retry without disposing the view twice.
        DisposeFullTextureView();

        // Swapchain images are owned by the swapchain. Retiring the
        // NeoVeldrid wrapper and its cached view must never destroy the
        // borrowed VkImage or return unowned memory.
        if (_isSwapchainTexture)
        {
            _destroyed = true;
            return;
        }

        // Clear each owned handle only after its release succeeds. If freeing
        // a later parent allocation fails, ResourceRefCount restores the final
        // reference and a retry resumes without destroying the native texture
        // object a second time.
        if (_stagingBuffer.Handle != 0)
        {
            _gd.Vk.DestroyBuffer(_gd.Device, _stagingBuffer, null);
            _stagingBuffer = default;
        }
        else if (_optimalImage.Handle != 0)
        {
            _gd.Vk.DestroyImage(_gd.Device, _optimalImage, null);
            _optimalImage = default;
        }

        if (_memoryBlock.DeviceMemory.Handle != 0)
        {
            _gd.MemoryManager.Free(_memoryBlock);
            _memoryBlock = default;
        }

        _destroyed = _stagingBuffer.Handle == 0
            && _optimalImage.Handle == 0
            && _memoryBlock.DeviceMemory.Handle == 0;
    }

    internal void SetImageLayout(
        uint mipLevel,
        uint arrayLayer,
        ImageLayout layout,
        VkImageLayoutTransaction transaction = null)
    {
        lock (VkImageLayoutTransaction.SyncRoot)
        {
            EnsureLayoutAccessAllowedLocked(transaction);
            uint subresource = CalculateSubresource(mipLevel, arrayLayer);
            transaction?.ObserveLocked(
                this,
                subresource,
                _imageLayouts[subresource],
                _imageLayoutRevisions[subresource]);
            if (_imageLayouts[subresource] != layout)
                SetImageLayoutStateLocked(subresource, layout);
        }
    }

    internal void RegisterLayoutTransactionLocked(
        VkImageLayoutTransaction transaction)
    {
        int existingIndex = _pendingLayoutTransactions.IndexOf(transaction);
        if (existingIndex >= 0)
        {
            ValidateRecordingTransactionLocked(transaction);
            return;
        }

        if (_pendingLayoutTransactions.Count != 0)
        {
            VkImageLayoutTransaction latestTransaction =
                _pendingLayoutTransactions[^1];
            if (latestTransaction.RecordingOrder >= transaction.RecordingOrder)
            {
                throw new NeoVeldridException(
                    "A Vulkan image-layout transaction cannot acquire a Texture after a later recording transaction has already used it. " +
                    "Finish recording command lists in one global order to avoid cyclic submission dependencies.");
            }
        }

        _pendingLayoutTransactions.Add(transaction);
    }

    internal void ValidateRecordingTransactionLocked(
        VkImageLayoutTransaction transaction)
    {
        if (_pendingLayoutTransactions.Count == 0 ||
            !ReferenceEquals(_pendingLayoutTransactions[^1], transaction))
        {
            throw new NeoVeldridException(
                "A Vulkan command list cannot continue recording image-layout assumptions after a later command list has used the same texture.");
        }
    }

    internal void ValidateSubmittingTransactionLocked(
        VkImageLayoutTransaction transaction)
    {
        if (_pendingLayoutTransactions.Count == 0 ||
            !ReferenceEquals(_pendingLayoutTransactions[0], transaction))
        {
            throw new NeoVeldridException(
                "Vulkan command lists which use the same texture must be submitted in recording order.");
        }
    }

    internal void CommitLayoutTransactionLocked(
        VkImageLayoutTransaction transaction)
    {
        ValidateSubmittingTransactionLocked(transaction);
        _pendingLayoutTransactions.RemoveAt(0);
    }

    internal void ValidateRollingBackTransactionLocked(
        VkImageLayoutTransaction transaction)
    {
        if (_pendingLayoutTransactions.Count == 0 ||
            !ReferenceEquals(_pendingLayoutTransactions[^1], transaction))
        {
            throw new NeoVeldridException(
                "A Vulkan command list cannot be abandoned while a later recording depends on its image-layout projections.");
        }
    }

    internal void RollbackLayoutTransactionLocked(
        VkImageLayoutTransaction transaction)
    {
        ValidateRollingBackTransactionLocked(transaction);
        _pendingLayoutTransactions.RemoveAt(_pendingLayoutTransactions.Count - 1);
    }

    internal void RestoreImageLayoutStateLocked(
        uint subresource,
        ImageLayout layout,
        uint revision)
    {
        _imageLayouts[subresource] = layout;
        _imageLayoutRevisions[subresource] = revision;
    }

    private void ObserveLayoutRangeLocked(
        VkImageLayoutTransaction transaction,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount)
    {
        if (transaction == null)
            return;

        for (uint level = 0; level < levelCount; level++)
        {
            for (uint layer = 0; layer < layerCount; layer++)
            {
                uint subresource = CalculateSubresource(
                    baseMipLevel + level,
                    baseArrayLayer + layer);
                transaction.ObserveLocked(
                    this,
                    subresource,
                    _imageLayouts[subresource],
                    _imageLayoutRevisions[subresource]);
            }
        }
    }

    private void EnsureLayoutAccessAllowedLocked(
        VkImageLayoutTransaction transaction)
    {
        if (transaction == null && _pendingLayoutTransactions.Count != 0)
        {
            throw new NeoVeldridException(
                "An immediate Vulkan texture operation cannot overtake a recorded command list which uses the same texture.");
        }
    }

    private void SetImageLayoutStateLocked(
        uint subresource,
        ImageLayout layout)
    {
        _imageLayouts[subresource] = layout;
        _imageLayoutRevisions[subresource] =
            unchecked(_imageLayoutRevisions[subresource] + 1u);
    }

    internal ImageAspectFlags ImageAspectMask
    {
        get
        {
            if ((Usage & TextureUsage.DepthStencil) == 0)
                return ImageAspectFlags.ColorBit;

            return FormatHelpers.IsStencilFormat(Format)
                ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                : ImageAspectFlags.DepthBit;
        }
    }
}
