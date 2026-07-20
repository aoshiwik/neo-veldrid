using Silk.NET.Vulkan;
using static NeoVeldrid.Vk.VulkanUtil;
using System;
using System.Collections.Generic;
using VkFramebufferHandle = Silk.NET.Vulkan.Framebuffer;

namespace NeoVeldrid.Vk;

internal unsafe class VkSwapchainFramebuffer : VkFramebufferBase
{
    private readonly VkGraphicsDevice _gd;
    private readonly VkSwapchain _swapchain;
    private readonly SurfaceKHR _surface;
    private readonly PixelFormat? _depthFormat;
    private uint _currentImageIndex;

    private VkFramebuffer[] _scFramebuffers;
    private Image[] _scImages = {};
    private Format _scImageFormat;
    private Extent2D _scExtent;
    private FramebufferAttachment[][] _scColorTextures;
    private bool[] _scColorTextureReleaseRequested;

    private FramebufferAttachment? _depthAttachment;
    private bool _depthAttachmentReleaseRequested;
    private uint _desiredWidth;
    private uint _desiredHeight;
    private bool _destroyed;
    private string _name;
    private OutputDescription _outputDescription;

    public override VkFramebufferHandle CurrentFramebuffer => _scFramebuffers[(int)_currentImageIndex].CurrentFramebuffer;

    public override RenderPass RenderPassNoClear_Init => _scFramebuffers[0].RenderPassNoClear_Init;
    public override RenderPass RenderPassNoClear_Load => _scFramebuffers[0].RenderPassNoClear_Load;
    public override RenderPass RenderPassClear => _scFramebuffers[0].RenderPassClear;

    public override IReadOnlyList<FramebufferAttachment> ColorTargets => _scColorTextures[(int)_currentImageIndex];

    public override FramebufferAttachment? DepthTarget => _depthAttachment;

    public override uint RenderableWidth => _scExtent.Width;
    public override uint RenderableHeight => _scExtent.Height;

    public override uint Width => _desiredWidth;
    public override uint Height => _desiredHeight;

    public uint ImageIndex => _currentImageIndex;

    public override OutputDescription OutputDescription => _outputDescription;

    public override uint AttachmentCount { get; }

    public VkSwapchain Swapchain => _swapchain;

    public override bool IsDisposed => _destroyed;

    public VkSwapchainFramebuffer(
        VkGraphicsDevice gd,
        VkSwapchain swapchain,
        SurfaceKHR surface,
        uint width,
        uint height,
        PixelFormat? depthFormat)
        : base()
    {
        _gd = gd;
        _swapchain = swapchain;
        _surface = surface;
        _depthFormat = depthFormat;
        _desiredWidth = width;
        _desiredHeight = height;

        AttachmentCount = depthFormat.HasValue ? 2u : 1u; // 1 Color + 1 Depth
    }

    internal void SetImageIndex(uint index)
    {
        _currentImageIndex = index;
    }

    internal void SetNewSwapchain(
        SwapchainKHR deviceSwapchain,
        uint width,
        uint height,
        SurfaceFormatKHR surfaceFormat,
        Extent2D swapchainExtent)
    {
        if (_destroyed)
        {
            throw new ObjectDisposedException(nameof(VkSwapchainFramebuffer));
        }
        if (_scFramebuffers != null || _depthAttachment != null)
        {
            throw new InvalidOperationException(
                "A VkSwapchainFramebuffer must be empty before it is initialized for a swapchain.");
        }

        _desiredWidth = width;
        _desiredHeight = height;

        // Get the images
        uint scImageCount = 0;
        Result result = _gd.KhrSwapchain.GetSwapchainImages(_gd.Device, deviceSwapchain, ref scImageCount, null);
        CheckResult(result);
        if (scImageCount == 0)
        {
            throw new NeoVeldridException("The Vulkan swapchain did not expose any images.");
        }

        _scImages = new Image[(int)scImageCount];
        result = _gd.KhrSwapchain.GetSwapchainImages(_gd.Device, deviceSwapchain, ref scImageCount, out _scImages[0]);
        CheckResult(result);

        _scImageFormat = surfaceFormat.Format;
        _scExtent = swapchainExtent;

        CreateDepthTexture();
        CreateFramebuffers();

        _outputDescription = OutputDescription.CreateFromFramebuffer(this);
    }

    /// <summary>
    /// Atomically exchanges the native framebuffer graphs of two wrappers for
    /// the same logical swapchain. This keeps the public framebuffer identity
    /// stable while a fully prepared replacement is committed.
    /// </summary>
    internal void SwapStateWith(VkSwapchainFramebuffer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_destroyed || other._destroyed)
        {
            throw new ObjectDisposedException(nameof(VkSwapchainFramebuffer));
        }
        if (!ReferenceEquals(_gd, other._gd)
            || !ReferenceEquals(_swapchain, other._swapchain)
            || _surface.Handle != other._surface.Handle
            || _depthFormat != other._depthFormat)
        {
            throw new InvalidOperationException(
                "Only framebuffer states belonging to the same Vulkan swapchain can be exchanged.");
        }

        (_scFramebuffers, other._scFramebuffers) = (other._scFramebuffers, _scFramebuffers);
        (_scImages, other._scImages) = (other._scImages, _scImages);
        (_scImageFormat, other._scImageFormat) = (other._scImageFormat, _scImageFormat);
        (_scExtent, other._scExtent) = (other._scExtent, _scExtent);
        (_scColorTextures, other._scColorTextures) = (other._scColorTextures, _scColorTextures);
        (_scColorTextureReleaseRequested, other._scColorTextureReleaseRequested) =
            (other._scColorTextureReleaseRequested, _scColorTextureReleaseRequested);
        (_depthAttachment, other._depthAttachment) = (other._depthAttachment, _depthAttachment);
        (_depthAttachmentReleaseRequested, other._depthAttachmentReleaseRequested) =
            (other._depthAttachmentReleaseRequested, _depthAttachmentReleaseRequested);
        (_desiredWidth, other._desiredWidth) = (other._desiredWidth, _desiredWidth);
        (_desiredHeight, other._desiredHeight) = (other._desiredHeight, _desiredHeight);
        (_currentImageIndex, other._currentImageIndex) = (other._currentImageIndex, _currentImageIndex);
        (_outputDescription, other._outputDescription) = (other._outputDescription, _outputDescription);
    }

    private void CreateDepthTexture()
    {
        if (_depthFormat.HasValue)
        {
            VkTexture depthTexture = (VkTexture)_gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                Math.Max(1, _scExtent.Width),
                Math.Max(1, _scExtent.Height),
                1,
                1,
                _depthFormat.Value,
                TextureUsage.DepthStencil));
            _depthAttachment = new FramebufferAttachment(depthTexture, 0);
        }
    }

    private void CreateFramebuffers()
    {
        _scFramebuffers = new VkFramebuffer[_scImages.Length];
        _scColorTextures = new FramebufferAttachment[_scImages.Length][];
        _scColorTextureReleaseRequested = new bool[_scImages.Length];
        for (uint i = 0; i < _scImages.Length; i++)
        {
            VkTexture colorTex = new VkTexture(
                _gd,
                Math.Max(1, _scExtent.Width),
                Math.Max(1, _scExtent.Height),
                1,
                1,
                _scImageFormat,
                TextureUsage.RenderTarget,
                TextureSampleCount.Count1,
                _scImages[i]);
            _scColorTextures[i] = new FramebufferAttachment[]
            {
                new FramebufferAttachment(colorTex, 0)
            };
            FramebufferDescription desc = new FramebufferDescription(_depthAttachment?.Target, colorTex);
            VkFramebuffer fb = new VkFramebuffer(_gd, ref desc, true);
            _scFramebuffers[i] = fb;
        }
    }

    public override void PrepareForRenderPass(
        CommandBuffer cb,
        VkRenderPassInitialLayoutKind initialLayoutKind,
        VkImageLayoutTransaction transaction)
    {
        _scFramebuffers[(int)_currentImageIndex].PrepareForRenderPass(
            cb,
            initialLayoutKind,
            transaction);
    }

    public override void RecordRenderPassFinalLayouts(
        CommandBuffer cb,
        VkImageLayoutTransaction transaction)
        => _scFramebuffers[(int)_currentImageIndex]
            .RecordRenderPassFinalLayouts(cb, transaction);

    public override void TransitionToExternalLayouts(
        CommandBuffer cb,
        VkImageLayoutTransaction transaction)
    {
        _scFramebuffers[(int)_currentImageIndex]
            .TransitionToExternalLayouts(cb, transaction);

        for (int i = 0; i < ColorTargets.Count; i++)
        {
            FramebufferAttachment ca = ColorTargets[i];
            VkTexture vkTex = Util.AssertSubtype<Texture, VkTexture>(ca.Target);
            vkTex.TransitionImageLayout(
                cb,
                0,
                1,
                ca.ArrayLayer,
                1,
                ImageLayout.PresentSrcKhr,
                transaction);
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

    protected override void DisposeCore()
    {
        if (!_destroyed)
        {
            VulkanCleanupCollector cleanup = new VulkanCleanupCollector();
            bool framebuffersReleased = true;
            if (_scFramebuffers != null)
            {
                for (int i = 0; i < _scFramebuffers.Length; i++)
                {
                    VkFramebuffer framebuffer = _scFramebuffers[i];
                    if (framebuffer != null)
                    {
                        bool framebufferReleased = framebuffer.IsDisposed
                            || (cleanup.Attempt(framebuffer.Dispose)
                                && framebuffer.IsDisposed);
                        if (framebufferReleased)
                        {
                            _scFramebuffers[i] = null;
                        }
                        else
                        {
                            framebuffersReleased = false;
                            if (!framebuffer.IsDisposed)
                            {
                                cleanup.Add(new InvalidOperationException(
                                    "A Vulkan framebuffer retained native children during swapchain cleanup."));
                            }
                        }
                    }
                }
            }

            if (framebuffersReleased)
            {
                _scFramebuffers = null;
            }

            // Each swapchain color VkTexture starts with one wrapper-owned
            // reference in addition to any transient command-list references.
            // Retire that initial reference after the VkFramebuffer image
            // views are gone, and wait for all transient owners before the
            // parent swapchain is allowed to destroy the borrowed VkImages.
            bool colorTexturesReleased = framebuffersReleased;
            if (framebuffersReleased && _scColorTextures != null)
            {
                for (int i = 0; i < _scColorTextures.Length; i++)
                {
                    FramebufferAttachment[] colorAttachments = _scColorTextures[i];
                    if (colorAttachments == null || colorAttachments.Length == 0)
                        continue;

                    Texture colorTexture = colorAttachments[0].Target;
                    bool colorTextureReleased = colorTexture.IsDisposed;
                    if (!colorTextureReleased
                        && !_scColorTextureReleaseRequested[i])
                    {
                        bool releaseRequested = cleanup.Attempt(colorTexture.Dispose);
                        _scColorTextureReleaseRequested[i] = releaseRequested;
                        colorTextureReleased = releaseRequested
                            && colorTexture.IsDisposed;
                    }

                    if (!colorTextureReleased)
                    {
                        colorTexturesReleased = false;
                        if (_scColorTextureReleaseRequested[i])
                        {
                            cleanup.Add(new InvalidOperationException(
                                "A Vulkan swapchain color texture retained native references during cleanup."));
                        }
                    }
                }
            }

            if (colorTexturesReleased)
            {
                _scColorTextures = null;
                _scColorTextureReleaseRequested = null;
                _scImages = Array.Empty<Image>();
            }

            // VkFramebuffer objects reference the depth image and its view, so
            // the depth texture is retired only after every framebuffer child.
            if (framebuffersReleased && _depthAttachment != null)
            {
                Texture depthTexture = _depthAttachment.Value.Target;
                bool depthReleased = depthTexture.IsDisposed;
                if (!depthReleased && !_depthAttachmentReleaseRequested)
                {
                    bool releaseRequested = cleanup.Attempt(depthTexture.Dispose);
                    _depthAttachmentReleaseRequested = releaseRequested;
                    depthReleased = releaseRequested && depthTexture.IsDisposed;
                }
                if (depthReleased)
                {
                    _depthAttachment = null;
                    _depthAttachmentReleaseRequested = false;
                }
                else if (_depthAttachmentReleaseRequested)
                {
                    cleanup.Add(new InvalidOperationException(
                        "The Vulkan swapchain depth texture retained native resources during cleanup."));
                }
            }

            _destroyed = framebuffersReleased
                && colorTexturesReleased
                && _depthAttachment == null;
            cleanup.ThrowIfAny(
                "Vulkan swapchain framebuffer cleanup encountered multiple failures.");
        }
    }
}
