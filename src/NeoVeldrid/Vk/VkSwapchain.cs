using System.Linq;
using Silk.NET.Vulkan;
using Silk.NET.Core;
using static NeoVeldrid.Vk.VulkanUtil;
using System;
using System.Collections.Generic;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;
using VkFenceHandle = Silk.NET.Vulkan.Fence;
using VkQueue = Silk.NET.Vulkan.Queue;

namespace NeoVeldrid.Vk;

internal unsafe class VkSwapchain : Swapchain
{
    private readonly VkGraphicsDevice _gd;
    private readonly SurfaceKHR _surface;
    private SwapchainKHR _deviceSwapchain;
    private readonly VkSwapchainFramebuffer _framebuffer;
    private VkFenceHandle _imageAvailableFence;
    private readonly uint _presentQueueIndex;
    private readonly VkQueue _presentQueue;
    private bool _syncToVBlank;
    private readonly SwapchainSource _swapchainSource;
    private readonly bool _colorSrgb;
    private readonly PixelFormat? _depthFormat;
    private bool? _newSyncToVBlank;
    private uint _currentImageIndex;
    private string _name;
    private bool _disposed;
    private bool _surfaceDestroyed;
    private bool _ownsSurface;
    private bool _framebufferReleaseRequested;
    private readonly List<RetiredSwapchainResources> _retiredSwapchains =
        new List<RetiredSwapchainResources>();

    public override string Name { get => _name; set { _name = value; _gd.SetResourceName(this, value); } }
    public override Framebuffer Framebuffer => _framebuffer;
    public override bool SyncToVerticalBlank
    {
        get => _newSyncToVBlank ?? _syncToVBlank;
        set
        {
            _newSyncToVBlank = value == _syncToVBlank ? null : value;
        }
    }

    public override bool IsDisposed => _disposed;

    public SwapchainKHR DeviceSwapchain => _deviceSwapchain;
    public uint ImageIndex => _currentImageIndex;
    public VkFenceHandle ImageAvailableFence => _imageAvailableFence;
    public SurfaceKHR Surface => _surface;
    public VkQueue PresentQueue => _presentQueue;
    public uint PresentQueueIndex => _presentQueueIndex;
    public ResourceRefCount RefCount { get; }

    public VkSwapchain(VkGraphicsDevice gd, ref SwapchainDescription description) : this(gd, ref description, default) { }

    public VkSwapchain(VkGraphicsDevice gd, ref SwapchainDescription description, SurfaceKHR existingSurface)
    {
        bool ownsSurfaceOnFailure = false;
        try
        {
            _gd = gd;
            _syncToVBlank = description.SyncToVerticalBlank;
            _swapchainSource = description.Source;
            _colorSrgb = description.ColorSrgb;
            _depthFormat = description.DepthFormat;

            if (existingSurface.Handle == default)
            {
                _surface = VkSurfaceUtil.CreateSurface(gd, gd.Instance, _swapchainSource);
                ownsSurfaceOnFailure = true;
                _ownsSurface = true;
            }
            else
            {
                _surface = existingSurface;
            }

            if (!GetPresentQueueIndex(out _presentQueueIndex))
            {
                throw new NeoVeldridException($"The system does not support presenting the given Vulkan surface.");
            }
            _gd.Vk.GetDeviceQueue(_gd.Device, _presentQueueIndex, 0, out _presentQueue);

            _framebuffer = new VkSwapchainFramebuffer(
                gd,
                this,
                _surface,
                description.Width,
                description.Height,
                _depthFormat);

            CreateSwapchain(description.Width, description.Height);

            FenceCreateInfo fenceCI = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = 0
            };
            VkFenceHandle createdImageAvailableFence;
            Result fenceResult = _gd.Vk.CreateFence(
                _gd.Device,
                &fenceCI,
                null,
                out createdImageAvailableFence);
            CheckResult(fenceResult);
            _imageAvailableFence = createdImageAvailableFence;

            if (!AcquireNextImage(_gd.Device, default, _imageAvailableFence))
            {
                throw new NeoVeldridException(
                    "The initial Vulkan swapchain image could not be acquired.");
            }
            VkFenceHandle iaf = _imageAvailableFence;
            CheckResult(_gd.Vk.WaitForFences(
                _gd.Device,
                1,
                &iaf,
                true,
                ulong.MaxValue));
            CheckResult(_gd.Vk.ResetFences(_gd.Device, 1, &iaf));

            RefCount = new ResourceRefCount(DisposeCore);
        }
        catch (Exception initializationError)
        {
            FailConstruction(initializationError, ownsSurfaceOnFailure);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void FailConstruction(
        Exception initializationError,
        bool ownsSurfaceOnFailure)
    {
        VulkanCleanupCollector cleanup = new VulkanCleanupCollector();
        bool deviceIdle = _gd == null || _gd.Device.Handle == 0;
        if (!deviceIdle)
        {
            // AcquireNextImage may have associated the construction fence with
            // presentation work. Retire that work before any dependent handle.
            deviceIdle = cleanup.Attempt(_gd.WaitForDeviceIdleAndReclaimSubmissions);
        }

        if (deviceIdle && _imageAvailableFence.Handle != 0)
        {
            cleanup.Attempt(() =>
            {
                _gd.Vk.DestroyFence(_gd.Device, _imageAvailableFence, null);
                _imageAvailableFence = default;
            });
        }

        bool framebufferReleased = _framebuffer == null;
        if (deviceIdle
            && _framebuffer != null
            && !_framebufferReleaseRequested)
        {
            bool releaseRequested = cleanup.Attempt(_framebuffer.Dispose);
            _framebufferReleaseRequested = releaseRequested;
            framebufferReleased = releaseRequested && _framebuffer.IsDisposed;
        }
        if (deviceIdle && _framebuffer != null && !framebufferReleased)
        {
            cleanup.Add(new InvalidOperationException(
                "The failed Vulkan swapchain retained framebuffer children during cleanup."));
        }

        if (deviceIdle)
        {
            RetirePendingSwapchains(cleanup);
        }

        bool currentDeviceSwapchainReleased = _deviceSwapchain.Handle == 0;
        if (deviceIdle
            && framebufferReleased
            && !currentDeviceSwapchainReleased)
        {
            currentDeviceSwapchainReleased = cleanup.Attempt(() =>
            {
                _gd.KhrSwapchain.DestroySwapchain(
                    _gd.Device,
                    _deviceSwapchain,
                    null);
                _deviceSwapchain = default;
            });
        }

        bool allDeviceSwapchainsReleased =
            currentDeviceSwapchainReleased
            && _retiredSwapchains.Count == 0;

        if (ownsSurfaceOnFailure
            && allDeviceSwapchainsReleased
            && _surface.Handle != 0)
        {
            cleanup.Attempt(() =>
            {
                _gd.KhrSurface.DestroySurface(_gd.Instance, _surface, null);
                _surfaceDestroyed = true;
            });
        }

        if (HasAbandonedResources)
        {
            // A constructor cannot return an owner for a partially cleaned
            // graph. Transfer that ownership back to the device so its parent
            // handles remain gated until another idle cleanup pass succeeds.
            _gd?.RegisterAbandonedSwapchain(this);
        }

        cleanup.ThrowWithPrimary(
            initializationError,
            "Vulkan swapchain initialization and cleanup both failed.");
    }

    public override void Resize(uint width, uint height)
    {
        RecreateAndReacquire(width, height);
    }

    public bool AcquireNextImage(Device device, VkSemaphore semaphore, VkFenceHandle fence)
    {
        if (_newSyncToVBlank != null)
        {
            RecreateAndReacquire(_framebuffer.Width, _framebuffer.Height);
            return false;
        }

        uint imageIndex = 0;
        Result result = _gd.KhrSwapchain.AcquireNextImage(
            device,
            _deviceSwapchain,
            ulong.MaxValue,
            semaphore,
            fence,
            &imageIndex);
        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr)
        {
            CreateSwapchain(_framebuffer.Width, _framebuffer.Height);
            return false;
        }
        else if (result != Result.Success)
        {
            throw new NeoVeldridException("Could not acquire next image from the Vulkan swapchain.");
        }

        _currentImageIndex = imageIndex;
        _framebuffer.SetImageIndex(_currentImageIndex);
        return true;
    }

    private void RecreateAndReacquire(uint width, uint height)
    {
        if (CreateSwapchain(width, height))
        {
            if (AcquireNextImage(_gd.Device, default, _imageAvailableFence))
            {
                VkFenceHandle iaf2 = _imageAvailableFence;
                CheckResult(_gd.Vk.WaitForFences(
                    _gd.Device,
                    1,
                    &iaf2,
                    true,
                    ulong.MaxValue));
                CheckResult(_gd.Vk.ResetFences(_gd.Device, 1, &iaf2));
            }
        }
    }

    private bool CreateSwapchain(uint width, uint height)
    {
        // Obtain the surface capabilities first -- this will indicate whether the surface has been lost.
        Result result = _gd.KhrSurface.GetPhysicalDeviceSurfaceCapabilities(_gd.PhysicalDevice, _surface, out SurfaceCapabilitiesKHR surfaceCapabilities);
        if (result == Result.ErrorSurfaceLostKhr)
        {
            throw new NeoVeldridException($"The Swapchain's underlying surface has been lost.");
        }
        CheckResult(result);

        if (surfaceCapabilities.MinImageExtent.Width == 0 && surfaceCapabilities.MinImageExtent.Height == 0
            && surfaceCapabilities.MaxImageExtent.Width == 0 && surfaceCapabilities.MaxImageExtent.Height == 0)
        {
            return false;
        }

        if (_deviceSwapchain.Handle != default || _retiredSwapchains.Count != 0)
        {
            _gd.WaitForDeviceIdleAndReclaimSubmissions();

            VulkanCleanupCollector pendingRetirement = new VulkanCleanupCollector();
            RetirePendingSwapchains(pendingRetirement);
            pendingRetirement.ThrowIfAny(
                "Previously retired Vulkan swapchain resources could not be released.");
        }

        bool requestedSyncToVBlank = _newSyncToVBlank ?? _syncToVBlank;
        uint surfaceFormatCount = 0;
        result = _gd.KhrSurface.GetPhysicalDeviceSurfaceFormats(_gd.PhysicalDevice, _surface, ref surfaceFormatCount, null);
        CheckResult(result);
        if (surfaceFormatCount == 0)
        {
            throw new NeoVeldridException(
                "The Vulkan surface did not expose any supported formats.");
        }
        SurfaceFormatKHR[] formats = new SurfaceFormatKHR[surfaceFormatCount];
        result = _gd.KhrSurface.GetPhysicalDeviceSurfaceFormats(_gd.PhysicalDevice, _surface, ref surfaceFormatCount, out formats[0]);
        CheckResult(result);

        Format desiredFormat = _colorSrgb
            ? Format.B8G8R8A8Srgb
            : Format.B8G8R8A8Unorm;

        SurfaceFormatKHR surfaceFormat = new SurfaceFormatKHR();
        if (formats.Length == 1 && formats[0].Format == Format.Undefined)
        {
            surfaceFormat = new SurfaceFormatKHR { ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr, Format = desiredFormat };
        }
        else
        {
            foreach (SurfaceFormatKHR format in formats)
            {
                if (format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr && format.Format == desiredFormat)
                {
                    surfaceFormat = format;
                    break;
                }
            }
            if (surfaceFormat.Format == Format.Undefined)
            {
                if (_colorSrgb && surfaceFormat.Format != Format.R8G8B8A8Srgb)
                {
                    throw new NeoVeldridException($"Unable to create an sRGB Swapchain for this surface.");
                }

                surfaceFormat = formats[0];
            }
        }

        uint presentModeCount = 0;
        result = _gd.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_gd.PhysicalDevice, _surface, ref presentModeCount, null);
        CheckResult(result);
        if (presentModeCount == 0)
        {
            throw new NeoVeldridException(
                "The Vulkan surface did not expose any supported present modes.");
        }
        PresentModeKHR[] presentModes = new PresentModeKHR[presentModeCount];
        result = _gd.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_gd.PhysicalDevice, _surface, ref presentModeCount, out presentModes[0]);
        CheckResult(result);

        PresentModeKHR presentMode = PresentModeKHR.FifoKhr;

        if (requestedSyncToVBlank)
        {
            if (presentModes.Contains(PresentModeKHR.FifoRelaxedKhr))
            {
                presentMode = PresentModeKHR.FifoRelaxedKhr;
            }
        }
        else
        {
            if (presentModes.Contains(PresentModeKHR.MailboxKhr))
            {
                presentMode = PresentModeKHR.MailboxKhr;
            }
            else if (presentModes.Contains(PresentModeKHR.ImmediateKhr))
            {
                presentMode = PresentModeKHR.ImmediateKhr;
            }
        }

        uint maxImageCount = surfaceCapabilities.MaxImageCount == 0 ? uint.MaxValue : surfaceCapabilities.MaxImageCount;
        uint imageCount = Math.Min(maxImageCount, surfaceCapabilities.MinImageCount + 1);

        SwapchainCreateInfoKHR swapchainCI = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            PresentMode = presentMode,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = new Extent2D { Width = Util.Clamp(width, surfaceCapabilities.MinImageExtent.Width, surfaceCapabilities.MaxImageExtent.Width), Height = Util.Clamp(height, surfaceCapabilities.MinImageExtent.Height, surfaceCapabilities.MaxImageExtent.Height) },
            MinImageCount = imageCount,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit
        };

        FixedArray2<uint> queueFamilyIndices = new FixedArray2<uint>(_gd.GraphicsQueueIndex, _gd.PresentQueueIndex);

        if (_gd.GraphicsQueueIndex != _gd.PresentQueueIndex)
        {
            swapchainCI.ImageSharingMode = SharingMode.Concurrent;
            swapchainCI.QueueFamilyIndexCount = 2;
            swapchainCI.PQueueFamilyIndices = &queueFamilyIndices.First;
        }
        else
        {
            swapchainCI.ImageSharingMode = SharingMode.Exclusive;
            swapchainCI.QueueFamilyIndexCount = 0;
        }

        swapchainCI.PreTransform = SurfaceTransformFlagsKHR.IdentityBitKhr;
        swapchainCI.CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr;
        swapchainCI.Clipped = true;

        SwapchainKHR oldSwapchain = _deviceSwapchain;
        swapchainCI.OldSwapchain = oldSwapchain;

        SwapchainKHR replacementSwapchain = default;
        VkSwapchainFramebuffer replacementFramebuffer = null;
        VkSwapchainFramebuffer failedOldFramebuffer = null;
        bool nativeReplacementAttempted = false;
        bool nativeReplacementCreated = false;
        bool replacementCommitted = false;
        try
        {
            // Allocate every managed owner before passing oldSwapchain to
            // vkCreateSwapchainKHR. That call retires oldSwapchain even when it
            // fails, so no fallible owner allocation may remain afterward.
            replacementFramebuffer = new VkSwapchainFramebuffer(
                _gd,
                this,
                _surface,
                width,
                height,
                _depthFormat);
            if (oldSwapchain.Handle != 0)
            {
                failedOldFramebuffer = new VkSwapchainFramebuffer(
                    _gd,
                    this,
                    _surface,
                    _framebuffer.Width,
                    _framebuffer.Height,
                    _depthFormat);
            }

            SwapchainKHR createdSwapchain;
            nativeReplacementAttempted = true;
            result = _gd.KhrSwapchain.CreateSwapchain(
                _gd.Device,
                &swapchainCI,
                null,
                out createdSwapchain);
            CheckResult(result);
            replacementSwapchain = createdSwapchain;
            nativeReplacementCreated = true;

            replacementFramebuffer.SetNewSwapchain(
                replacementSwapchain,
                width,
                height,
                surfaceFormat,
                swapchainCI.ImageExtent);

            // All validation is performed before the exchange. The existing
            // wrapper identity remains stable for callers that cache
            // Swapchain.Framebuffer.
            _framebuffer.SwapStateWith(replacementFramebuffer);
            replacementCommitted = true;
            _deviceSwapchain = replacementSwapchain;
            _currentImageIndex = 0;
            _framebuffer.SetImageIndex(0);
            _syncToVBlank = requestedSyncToVBlank;
            _newSyncToVBlank = null;
        }
        catch (Exception replacementError)
        {
            VulkanCleanupCollector cleanup = new VulkanCleanupCollector();
            if (replacementCommitted)
            {
                cleanup.ThrowWithPrimary(
                    replacementError,
                    "The Vulkan swapchain replacement committed before a later operation failed.");
            }

            // The Vulkan contract retires oldSwapchain at invocation time,
            // including error returns. Once the call was attempted, remove
            // the retired chain from the live fields instead of pretending it
            // can be restored.
            if (nativeReplacementAttempted && oldSwapchain.Handle != 0)
            {
                _framebuffer.SwapStateWith(failedOldFramebuffer);
                _deviceSwapchain = default;
                _currentImageIndex = 0;
                _framebuffer.SetImageIndex(0);
                _retiredSwapchains.Add(new RetiredSwapchainResources(
                    failedOldFramebuffer,
                    oldSwapchain));
                failedOldFramebuffer = null;
            }

            if (nativeReplacementCreated)
            {
                _retiredSwapchains.Add(new RetiredSwapchainResources(
                    replacementFramebuffer,
                    replacementSwapchain));
                replacementFramebuffer = null;
            }

            if (replacementFramebuffer != null)
            {
                cleanup.Attempt(replacementFramebuffer.Dispose);
            }
            if (failedOldFramebuffer != null)
            {
                cleanup.Attempt(failedOldFramebuffer.Dispose);
            }

            // Preparing replacement framebuffer textures may submit work.
            // A failed native create does not, and the old graph was already
            // proved idle before this replacement attempt.
            bool replacementIdle = !nativeReplacementCreated
                || cleanup.Attempt(_gd.WaitForDeviceIdleAndReclaimSubmissions);
            if (replacementIdle)
            {
                RetirePendingSwapchains(cleanup);
            }

            cleanup.ThrowWithPrimary(
                replacementError,
                "Vulkan swapchain replacement failed while retiring its native ownership graph.");
        }

        VulkanCleanupCollector retirement = new VulkanCleanupCollector();
        if (failedOldFramebuffer != null)
        {
            retirement.Attempt(failedOldFramebuffer.Dispose);
        }
        // After the state exchange, this wrapper owns the old framebuffer
        // children (or an empty graph during initial construction).
        if (oldSwapchain.Handle != default)
        {
            _retiredSwapchains.Add(new RetiredSwapchainResources(
                replacementFramebuffer,
                oldSwapchain));
            RetirePendingSwapchains(retirement);
        }
        else if (!retirement.Attempt(replacementFramebuffer.Dispose)
            || !replacementFramebuffer.IsDisposed)
        {
            retirement.Add(new InvalidOperationException(
                "The initial Vulkan swapchain retained its empty staging framebuffer wrapper."));
        }

        retirement.ThrowIfAny(
            "The Vulkan swapchain replacement committed, but retiring its previous native graph failed.");
        return true;
    }

    private bool GetPresentQueueIndex(out uint queueFamilyIndex)
    {
        uint graphicsQueueIndex = _gd.GraphicsQueueIndex;
        uint presentQueueIndex = _gd.PresentQueueIndex;

        if (QueueSupportsPresent(graphicsQueueIndex, _surface))
        {
            queueFamilyIndex = graphicsQueueIndex;
            return true;
        }
        else if (graphicsQueueIndex != presentQueueIndex && QueueSupportsPresent(presentQueueIndex, _surface))
        {
            queueFamilyIndex = presentQueueIndex;
            return true;
        }

        queueFamilyIndex = 0;
        return false;
    }

    private bool QueueSupportsPresent(uint queueFamilyIndex, SurfaceKHR surface)
    {
        Result result = _gd.KhrSurface.GetPhysicalDeviceSurfaceSupport(
            _gd.PhysicalDevice,
            queueFamilyIndex,
            surface,
            out Bool32 supported);
        CheckResult(result);
        return supported;
    }

    public override void Dispose()
    {
        RefCount.Decrement();
    }

    private void DisposeCore()
    {
        VulkanCleanupCollector cleanup = new VulkanCleanupCollector();
        if (_imageAvailableFence.Handle != 0)
        {
            cleanup.Attempt(() =>
            {
                _gd.Vk.DestroyFence(_gd.Device, _imageAvailableFence, null);
                _imageAvailableFence = default;
            });
        }

        RetirePendingSwapchains(cleanup);

        bool framebufferReleased = _framebuffer.IsDisposed;
        if (!framebufferReleased && !_framebufferReleaseRequested)
        {
            bool releaseRequested = cleanup.Attempt(_framebuffer.Dispose);
            _framebufferReleaseRequested = releaseRequested;
            framebufferReleased = releaseRequested && _framebuffer.IsDisposed;
        }
        if (!framebufferReleased)
        {
            cleanup.Add(new InvalidOperationException(
                "The Vulkan swapchain framebuffer still has outstanding references during cleanup."));
        }

        bool deviceSwapchainReleased = _deviceSwapchain.Handle == 0;
        if (framebufferReleased && !deviceSwapchainReleased)
        {
            deviceSwapchainReleased = cleanup.Attempt(() =>
            {
                _gd.KhrSwapchain.DestroySwapchain(
                    _gd.Device,
                    _deviceSwapchain,
                    null);
                _deviceSwapchain = default;
            });
        }
        if (deviceSwapchainReleased
            && _retiredSwapchains.Count == 0
            && !_surfaceDestroyed
            && _surface.Handle != 0)
        {
            cleanup.Attempt(() =>
            {
                _gd.KhrSurface.DestroySurface(
                    _gd.Instance,
                    _surface,
                    null);
                _surfaceDestroyed = true;
            });
        }

        _disposed = _imageAvailableFence.Handle == 0
            && framebufferReleased
            && deviceSwapchainReleased
            && _retiredSwapchains.Count == 0
            && (_surface.Handle == 0 || _surfaceDestroyed);
        cleanup.ThrowIfAny("Vulkan swapchain cleanup encountered multiple failures.");
    }

    private bool HasAbandonedResources =>
        _imageAvailableFence.Handle != 0
        || (_framebuffer != null && !_framebuffer.IsDisposed)
        || _deviceSwapchain.Handle != 0
        || _retiredSwapchains.Count != 0
        || (_ownsSurface && !_surfaceDestroyed && _surface.Handle != 0);

    internal bool TryReleaseAbandonedDeviceResources(
        VulkanCleanupCollector cleanup)
    {
        if (_imageAvailableFence.Handle != 0)
        {
            cleanup.Attempt(() =>
            {
                _gd.Vk.DestroyFence(_gd.Device, _imageAvailableFence, null);
                _imageAvailableFence = default;
            });
        }

        RetirePendingSwapchains(cleanup);

        bool framebufferReleased = _framebuffer == null || _framebuffer.IsDisposed;
        if (!framebufferReleased && !_framebufferReleaseRequested)
        {
            bool releaseRequested = cleanup.Attempt(_framebuffer.Dispose);
            _framebufferReleaseRequested = releaseRequested;
            framebufferReleased = releaseRequested && _framebuffer.IsDisposed;
        }
        if (!framebufferReleased)
        {
            cleanup.Add(new InvalidOperationException(
                "An abandoned Vulkan swapchain retained framebuffer children."));
        }

        if (framebufferReleased
            && _retiredSwapchains.Count == 0
            && _deviceSwapchain.Handle != 0)
        {
            cleanup.Attempt(() =>
            {
                _gd.KhrSwapchain.DestroySwapchain(
                    _gd.Device,
                    _deviceSwapchain,
                    null);
                _deviceSwapchain = default;
            });
        }

        return _imageAvailableFence.Handle == 0
            && framebufferReleased
            && _retiredSwapchains.Count == 0
            && _deviceSwapchain.Handle == 0;
    }

    internal bool TryReleaseAbandonedInstanceResources(
        VulkanCleanupCollector cleanup)
    {
        if (_imageAvailableFence.Handle != 0
            || (_framebuffer != null && !_framebuffer.IsDisposed)
            || _retiredSwapchains.Count != 0
            || _deviceSwapchain.Handle != 0)
        {
            return false;
        }

        if (_ownsSurface && !_surfaceDestroyed && _surface.Handle != 0)
        {
            cleanup.Attempt(() =>
            {
                _gd.KhrSurface.DestroySurface(_gd.Instance, _surface, null);
                _surfaceDestroyed = true;
            });
        }

        return !_ownsSurface || _surfaceDestroyed || _surface.Handle == 0;
    }

    private void RetirePendingSwapchains(VulkanCleanupCollector cleanup)
    {
        for (int i = _retiredSwapchains.Count - 1; i >= 0; i--)
        {
            if (_retiredSwapchains[i].TryRetire(_gd, cleanup))
            {
                _retiredSwapchains.RemoveAt(i);
            }
        }
    }

    private sealed class RetiredSwapchainResources
    {
        private readonly VkSwapchainFramebuffer _framebuffer;
        private SwapchainKHR _swapchain;
        private bool _framebufferReleaseRequested;

        internal RetiredSwapchainResources(
            VkSwapchainFramebuffer framebuffer,
            SwapchainKHR swapchain,
            bool framebufferReleaseRequested = false)
        {
            _framebuffer = framebuffer;
            _swapchain = swapchain;
            _framebufferReleaseRequested = framebufferReleaseRequested;
        }

        internal bool TryRetire(
            VkGraphicsDevice gd,
            VulkanCleanupCollector cleanup)
        {
            if (!_framebufferReleaseRequested)
            {
                bool releaseRequested = _framebuffer.IsDisposed
                    || cleanup.Attempt(_framebuffer.Dispose);
                _framebufferReleaseRequested = releaseRequested;
                if (!releaseRequested)
                {
                    return false;
                }
            }

            if (!_framebuffer.IsDisposed)
            {
                cleanup.Add(new InvalidOperationException(
                    "A retired Vulkan swapchain still has live framebuffer children."));
                return false;
            }

            if (_swapchain.Handle != 0
                && !cleanup.Attempt(() =>
                {
                    gd.KhrSwapchain.DestroySwapchain(
                        gd.Device,
                        _swapchain,
                        null);
                    _swapchain = default;
                }))
            {
                return false;
            }

            return true;
        }
    }
}
