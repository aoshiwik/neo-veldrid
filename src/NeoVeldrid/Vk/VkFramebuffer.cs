using System.Collections.Generic;
using Silk.NET.Vulkan;
using static NeoVeldrid.Vk.VulkanUtil;
using System;
using System.Diagnostics;
using VkFramebufferHandle = Silk.NET.Vulkan.Framebuffer;

namespace NeoVeldrid.Vk;

internal unsafe class VkFramebuffer : VkFramebufferBase
{
    private readonly VkGraphicsDevice _gd;
    private VkFramebufferHandle _deviceFramebuffer;
    private RenderPass _renderPassNoClearLoad;
    private RenderPass _renderPassNoClear;
    private RenderPass _renderPassClear;
    private readonly List<ImageView> _attachmentViews = new List<ImageView>();
    private readonly ImageLayout[] _firstUseColorLayouts;
    private readonly ImageLayout? _firstUseDepthLayout;
    private bool _destroyed;
    private string _name;

    public override VkFramebufferHandle CurrentFramebuffer => _deviceFramebuffer;
    public override RenderPass RenderPassNoClear_Init => _renderPassNoClear;
    public override RenderPass RenderPassNoClear_Load => _renderPassNoClearLoad;
    public override RenderPass RenderPassClear => _renderPassClear;

    public override uint RenderableWidth => Width;
    public override uint RenderableHeight => Height;

    public override uint AttachmentCount { get; }

    public override bool IsDisposed => _destroyed;

    public VkFramebuffer(VkGraphicsDevice gd, ref FramebufferDescription description, bool isPresented)
        : base(description.DepthTarget, description.ColorTargets)
    {
        _gd = gd;
        try
        {
        RenderPassCreateInfo renderPassCI = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo
        };

        uint colorAttachmentCount = (uint)ColorTargets.Count;
        _firstUseColorLayouts = new ImageLayout[ColorTargets.Count];
        AttachmentDescription* attachments = stackalloc AttachmentDescription[(int)colorAttachmentCount + 1];
        uint attachmentCount = 0;
        AttachmentReference* colorAttachmentRefs = stackalloc AttachmentReference[(int)colorAttachmentCount];
        for (int i = 0; i < colorAttachmentCount; i++)
        {
            VkTexture vkColorTex = Util.AssertSubtype<Texture, VkTexture>(ColorTargets[i].Target);
            AttachmentDescription colorAttachmentDesc = new AttachmentDescription();
            colorAttachmentDesc.Format = vkColorTex.VkFormat;
            colorAttachmentDesc.Samples = vkColorTex.VkSampleCount;
            colorAttachmentDesc.LoadOp = AttachmentLoadOp.Load;
            colorAttachmentDesc.StoreOp = AttachmentStoreOp.Store;
            colorAttachmentDesc.StencilLoadOp = AttachmentLoadOp.DontCare;
            colorAttachmentDesc.StencilStoreOp = AttachmentStoreOp.DontCare;
            colorAttachmentDesc.InitialLayout = isPresented
                ? ImageLayout.PresentSrcKhr
                : ((vkColorTex.Usage & TextureUsage.Sampled) != 0)
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : ImageLayout.ColorAttachmentOptimal;
            _firstUseColorLayouts[i] = colorAttachmentDesc.InitialLayout;
            colorAttachmentDesc.FinalLayout = ImageLayout.ColorAttachmentOptimal;
            attachments[attachmentCount++] = colorAttachmentDesc;

            AttachmentReference colorAttachmentRef = new AttachmentReference();
            colorAttachmentRef.Attachment = (uint)i;
            colorAttachmentRef.Layout = ImageLayout.ColorAttachmentOptimal;
            colorAttachmentRefs[i] = colorAttachmentRef;
        }

        AttachmentDescription depthAttachmentDesc = new AttachmentDescription();
        AttachmentReference depthAttachmentRef = new AttachmentReference();
        if (DepthTarget != null)
        {
            VkTexture vkDepthTex = Util.AssertSubtype<Texture, VkTexture>(DepthTarget.Value.Target);
            bool hasStencil = FormatHelpers.IsStencilFormat(vkDepthTex.Format);
            depthAttachmentDesc.Format = vkDepthTex.VkFormat;
            depthAttachmentDesc.Samples = vkDepthTex.VkSampleCount;
            depthAttachmentDesc.LoadOp = AttachmentLoadOp.Load;
            depthAttachmentDesc.StoreOp = AttachmentStoreOp.Store;
            depthAttachmentDesc.StencilLoadOp = AttachmentLoadOp.DontCare;
            depthAttachmentDesc.StencilStoreOp = hasStencil
                ? AttachmentStoreOp.Store
                : AttachmentStoreOp.DontCare;
            depthAttachmentDesc.InitialLayout = ((vkDepthTex.Usage & TextureUsage.Sampled) != 0)
                ? ImageLayout.ShaderReadOnlyOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;
            _firstUseDepthLayout = depthAttachmentDesc.InitialLayout;
            depthAttachmentDesc.FinalLayout = ImageLayout.DepthStencilAttachmentOptimal;

            depthAttachmentRef.Attachment = (uint)description.ColorTargets.Length;
            depthAttachmentRef.Layout = ImageLayout.DepthStencilAttachmentOptimal;
        }

        SubpassDescription subpass = new SubpassDescription();
        subpass.PipelineBindPoint = PipelineBindPoint.Graphics;
        if (ColorTargets.Count > 0)
        {
            subpass.ColorAttachmentCount = colorAttachmentCount;
            subpass.PColorAttachments = colorAttachmentRefs;
        }

        if (DepthTarget != null)
        {
            subpass.PDepthStencilAttachment = &depthAttachmentRef;
            attachments[attachmentCount++] = depthAttachmentDesc;
        }

        SubpassDependency subpassDependency =
            CreateRenderPassAttachmentDependency(
                hasColorAttachments: colorAttachmentCount != 0,
                hasDepthStencilAttachment: DepthTarget != null);

        renderPassCI.AttachmentCount = attachmentCount;
        renderPassCI.PAttachments = attachments;
        renderPassCI.SubpassCount = 1;
        renderPassCI.PSubpasses = &subpass;
        renderPassCI.DependencyCount = 1;
        renderPassCI.PDependencies = &subpassDependency;

        RenderPass createdRenderPass;
        Result creationResult = _gd.Vk.CreateRenderPass(
            _gd.Device,
            in renderPassCI,
            null,
            out createdRenderPass);
        CheckResult(creationResult);
        _renderPassNoClear = createdRenderPass;

        for (int i = 0; i < colorAttachmentCount; i++)
        {
            attachments[i].LoadOp = AttachmentLoadOp.Load;
            attachments[i].InitialLayout = ImageLayout.ColorAttachmentOptimal;
        }
        if (DepthTarget != null)
        {
            attachments[attachmentCount - 1].LoadOp = AttachmentLoadOp.Load;
            attachments[attachmentCount - 1].InitialLayout = ImageLayout.DepthStencilAttachmentOptimal;
            bool hasStencil = FormatHelpers.IsStencilFormat(DepthTarget.Value.Target.Format);
            if (hasStencil)
            {
                attachments[attachmentCount - 1].StencilLoadOp = AttachmentLoadOp.Load;
            }

        }
        creationResult = _gd.Vk.CreateRenderPass(
            _gd.Device,
            in renderPassCI,
            null,
            out createdRenderPass);
        CheckResult(creationResult);
        _renderPassNoClearLoad = createdRenderPass;


        // Load version

        if (DepthTarget != null)
        {
            attachments[attachmentCount - 1].LoadOp = AttachmentLoadOp.Clear;
            attachments[attachmentCount - 1].InitialLayout = ImageLayout.Undefined;
            bool hasStencil = FormatHelpers.IsStencilFormat(DepthTarget.Value.Target.Format);
            if (hasStencil)
            {
                attachments[attachmentCount - 1].StencilLoadOp = AttachmentLoadOp.Clear;
            }
        }

        for (int i = 0; i < colorAttachmentCount; i++)
        {
            attachments[i].LoadOp = AttachmentLoadOp.Clear;
            attachments[i].InitialLayout = ImageLayout.Undefined;
        }

        creationResult = _gd.Vk.CreateRenderPass(
            _gd.Device,
            in renderPassCI,
            null,
            out createdRenderPass);
        CheckResult(creationResult);
        _renderPassClear = createdRenderPass;

        FramebufferCreateInfo fbCI = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo
        };
        uint fbAttachmentsCount = (uint)description.ColorTargets.Length;
        if (description.DepthTarget != null)
        {
            fbAttachmentsCount += 1;
        }

        ImageView* fbAttachments = stackalloc ImageView[(int)fbAttachmentsCount];
        for (int i = 0; i < colorAttachmentCount; i++)
        {
            VkTexture vkColorTarget = Util.AssertSubtype<Texture, VkTexture>(description.ColorTargets[i].Target);
            ImageViewCreateInfo imageViewCI = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = vkColorTarget.OptimalDeviceImage,
                Format = vkColorTarget.VkFormat,
                ViewType = ImageViewType.Type2D,
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.ColorBit,
                    description.ColorTargets[i].MipLevel,
                    1,
                    description.ColorTargets[i].ArrayLayer,
                    1)
            };
            ImageView createdView = default;
            Result result = _gd.Vk.CreateImageView(
                _gd.Device,
                in imageViewCI,
                null,
                &createdView);
            CheckResult(result);
            fbAttachments[i] = createdView;
            _attachmentViews.Add(createdView);
        }

        // Depth
        if (description.DepthTarget != null)
        {
            VkTexture vkDepthTarget = Util.AssertSubtype<Texture, VkTexture>(description.DepthTarget.Value.Target);
            bool hasStencil = FormatHelpers.IsStencilFormat(vkDepthTarget.Format);
            ImageViewCreateInfo depthViewCI = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = vkDepthTarget.OptimalDeviceImage,
                Format = vkDepthTarget.VkFormat,
                ViewType = description.DepthTarget.Value.Target.ArrayLayers == 1
                    ? ImageViewType.Type2D
                    : ImageViewType.Type2DArray,
                SubresourceRange = new ImageSubresourceRange(
                    hasStencil ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit,
                    description.DepthTarget.Value.MipLevel,
                    1,
                    description.DepthTarget.Value.ArrayLayer,
                    1)
            };
            ImageView createdView = default;
            Result result = _gd.Vk.CreateImageView(
                _gd.Device,
                in depthViewCI,
                null,
                &createdView);
            CheckResult(result);
            fbAttachments[fbAttachmentsCount - 1] = createdView;
            _attachmentViews.Add(createdView);
        }

        Texture dimTex;
        uint mipLevel;
        if (ColorTargets.Count > 0)
        {
            dimTex = ColorTargets[0].Target;
            mipLevel = ColorTargets[0].MipLevel;
        }
        else
        {
            Debug.Assert(DepthTarget != null);
            dimTex = DepthTarget.Value.Target;
            mipLevel = DepthTarget.Value.MipLevel;
        }

        Util.GetMipDimensions(
            dimTex,
            mipLevel,
            out uint mipWidth,
            out uint mipHeight,
            out _);

        fbCI.Width = mipWidth;
        fbCI.Height = mipHeight;

        fbCI.AttachmentCount = fbAttachmentsCount;
        fbCI.PAttachments = fbAttachments;
        fbCI.Layers = 1;
        fbCI.RenderPass = _renderPassNoClear;

        VkFramebufferHandle createdFramebuffer;
        creationResult = _gd.Vk.CreateFramebuffer(
            _gd.Device,
            in fbCI,
            null,
            out createdFramebuffer);
        CheckResult(creationResult);
        _deviceFramebuffer = createdFramebuffer;

        if (DepthTarget != null)
        {
            AttachmentCount += 1;
        }
        AttachmentCount += (uint)ColorTargets.Count;
        }
        catch (Exception initializationError)
        {
            DestroyNativeResources().ThrowWithPrimary(
                initializationError,
                "Vulkan framebuffer initialization and cleanup both failed.");
        }
    }

    public override void PrepareForRenderPass(
        CommandBuffer cb,
        VkRenderPassInitialLayoutKind initialLayoutKind,
        VkImageLayoutTransaction transaction)
    {
        for (int i = 0; i < ColorTargets.Count; i++)
        {
            FramebufferAttachment attachment = ColorTargets[i];
            VkTexture texture =
                Util.AssertSubtype<Texture, VkTexture>(attachment.Target);
            PrepareAttachment(
                cb,
                texture,
                attachment.MipLevel,
                attachment.ArrayLayer,
                initialLayoutKind,
                _firstUseColorLayouts[i],
                ImageLayout.ColorAttachmentOptimal,
                transaction);
        }

        if (DepthTarget != null)
        {
            FramebufferAttachment attachment = DepthTarget.Value;
            VkTexture texture =
                Util.AssertSubtype<Texture, VkTexture>(attachment.Target);
            PrepareAttachment(
                cb,
                texture,
                attachment.MipLevel,
                attachment.ArrayLayer,
                initialLayoutKind,
                _firstUseDepthLayout!.Value,
                ImageLayout.DepthStencilAttachmentOptimal,
                transaction);
        }
    }

    private static void PrepareAttachment(
        CommandBuffer cb,
        VkTexture texture,
        uint mipLevel,
        uint arrayLayer,
        VkRenderPassInitialLayoutKind initialLayoutKind,
        ImageLayout firstUseLayout,
        ImageLayout continuationLayout,
        VkImageLayoutTransaction transaction)
    {
        if (initialLayoutKind == VkRenderPassInitialLayoutKind.Discard)
        {
            // A clear render pass intentionally declares Undefined and discards
            // the previous contents. Observe the attachment without attempting
            // an invalid transition to Undefined so ordering and rollback still
            // include this render-pass use.
            _ = texture.GetImageLayout(mipLevel, arrayLayer, transaction);
            return;
        }

        ImageLayout requiredLayout =
            initialLayoutKind == VkRenderPassInitialLayoutKind.FirstUse
                ? firstUseLayout
                : continuationLayout;
        texture.TransitionImageLayout(
            cb,
            mipLevel,
            1,
            arrayLayer,
            1,
            requiredLayout,
            transaction);
    }

    public override void RecordRenderPassFinalLayouts(
        CommandBuffer cb,
        VkImageLayoutTransaction transaction)
    {
        for (int i = 0; i < ColorTargets.Count; i++)
        {
            FramebufferAttachment ca = ColorTargets[i];
            VkTexture vkTex = Util.AssertSubtype<Texture, VkTexture>(ca.Target);
            vkTex.SetImageLayout(
                ca.MipLevel,
                ca.ArrayLayer,
                ImageLayout.ColorAttachmentOptimal,
                transaction);
        }
        if (DepthTarget != null)
        {
            VkTexture vkTex = Util.AssertSubtype<Texture, VkTexture>(DepthTarget.Value.Target);
            vkTex.SetImageLayout(
                DepthTarget.Value.MipLevel,
                DepthTarget.Value.ArrayLayer,
                ImageLayout.DepthStencilAttachmentOptimal,
                transaction);
        }
    }

    public override void TransitionToExternalLayouts(
        CommandBuffer cb,
        VkImageLayoutTransaction transaction)
    {
        for (int i = 0; i < ColorTargets.Count; i++)
        {
            FramebufferAttachment ca = ColorTargets[i];
            VkTexture vkTex = Util.AssertSubtype<Texture, VkTexture>(ca.Target);
            if ((vkTex.Usage & TextureUsage.Sampled) != 0)
            {
                vkTex.TransitionImageLayout(
                    cb,
                    ca.MipLevel, 1,
                    ca.ArrayLayer, 1,
                    ImageLayout.ShaderReadOnlyOptimal,
                    transaction);
            }
        }
        if (DepthTarget != null)
        {
            VkTexture vkTex = Util.AssertSubtype<Texture, VkTexture>(DepthTarget.Value.Target);
            if ((vkTex.Usage & TextureUsage.Sampled) != 0)
            {
                vkTex.TransitionImageLayout(
                    cb,
                    DepthTarget.Value.MipLevel, 1,
                    DepthTarget.Value.ArrayLayer, 1,
                    ImageLayout.ShaderReadOnlyOptimal,
                    transaction);
            }
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
            VulkanCleanupCollector cleanup = DestroyNativeResources();
            _destroyed = NativeResourcesReleased;
            cleanup.ThrowIfAny(
                "Vulkan framebuffer cleanup encountered multiple failures.");
        }
    }

    private bool NativeResourcesReleased =>
        _deviceFramebuffer.Handle == 0
        && _attachmentViews.Count == 0
        && _renderPassNoClear.Handle == 0
        && _renderPassNoClearLoad.Handle == 0
        && _renderPassClear.Handle == 0;

    private VulkanCleanupCollector DestroyNativeResources()
    {
        VulkanCleanupCollector cleanup = new VulkanCleanupCollector();
        bool framebufferReleased = true;
        if (_deviceFramebuffer.Handle != 0)
        {
            framebufferReleased = cleanup.Attempt(() =>
            {
                _gd.Vk.DestroyFramebuffer(_gd.Device, _deviceFramebuffer, null);
                _deviceFramebuffer = default;
            });
        }

        // Image views and render passes are referenced by VkFramebuffer. Do not
        // retire them if that child could not be destroyed.
        if (framebufferReleased)
        {
            for (int i = _attachmentViews.Count - 1; i >= 0; i--)
            {
                ImageView view = _attachmentViews[i];
                if (cleanup.Attempt(() =>
                    _gd.Vk.DestroyImageView(_gd.Device, view, null)))
                {
                    _attachmentViews.RemoveAt(i);
                }
            }

            if (_renderPassNoClear.Handle != 0)
            {
                cleanup.Attempt(() =>
                {
                    _gd.Vk.DestroyRenderPass(_gd.Device, _renderPassNoClear, null);
                    _renderPassNoClear = default;
                });
            }
            if (_renderPassNoClearLoad.Handle != 0)
            {
                cleanup.Attempt(() =>
                {
                    _gd.Vk.DestroyRenderPass(_gd.Device, _renderPassNoClearLoad, null);
                    _renderPassNoClearLoad = default;
                });
            }
            if (_renderPassClear.Handle != 0)
            {
                cleanup.Attempt(() =>
                {
                    _gd.Vk.DestroyRenderPass(_gd.Device, _renderPassClear, null);
                    _renderPassClear = default;
                });
            }
        }

        return cleanup;
    }
}
