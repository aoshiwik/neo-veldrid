using System;

namespace NeoVeldrid;

/// <summary>
/// A device object responsible for the creation of graphics resources.
/// </summary>
public abstract class ResourceFactory
{
    /// <summary></summary>
    /// <param name="graphicsDevice">The device which owns the resources created by this factory.</param>
    protected ResourceFactory(GraphicsDevice graphicsDevice)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        GraphicsDevice = graphicsDevice;
    }

    /// <summary>
    /// Gets the <see cref="GraphicsDevice"/> whose capability contract governs this factory.
    /// </summary>
    public GraphicsDevice GraphicsDevice { get; }

    /// <summary>
    /// Gets the <see cref="GraphicsBackend"/> of this instance.
    /// </summary>
    public abstract GraphicsBackend BackendType { get; }

    /// <summary>
    /// Gets the <see cref="GraphicsDeviceFeatures"/> this instance was created with.
    /// </summary>
    public GraphicsDeviceFeatures Features => GraphicsDevice.Features;

    /// <summary>
    /// Creates a new <see cref="Pipeline"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Pipeline"/>.</returns>
    public Pipeline CreateGraphicsPipeline(GraphicsPipelineDescription description) => CreateGraphicsPipeline(ref description);
    /// <summary>
    /// Creates a new <see cref="Pipeline"/> object.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Pipeline"/> which, when bound to a CommandList, is used to dispatch draw commands.</returns>
    public Pipeline CreateGraphicsPipeline(ref GraphicsPipelineDescription description)
    {
#if VALIDATE_USAGE
        if (!description.RasterizerState.DepthClipEnabled && !Features.DepthClipDisable)
        {
            throw new NeoVeldridException(
                "RasterizerState.DepthClipEnabled must be true if GraphicsDeviceFeatures.DepthClipDisable is not supported.");
        }
        if (description.RasterizerState.FillMode == PolygonFillMode.Wireframe && !Features.FillModeWireframe)
        {
            throw new NeoVeldridException(
                "PolygonFillMode.Wireframe requires GraphicsDeviceFeatures.FillModeWireframe.");
        }
        if (!Features.IndependentBlend)
        {
            if (description.BlendState.AttachmentStates.Length > 0)
            {
                BlendAttachmentDescription attachmentState = description.BlendState.AttachmentStates[0];
                for (int i = 1; i < description.BlendState.AttachmentStates.Length; i++)
                {
                    if (!attachmentState.Equals(description.BlendState.AttachmentStates[i]))
                    {
                        throw new NeoVeldridException(
                            $"If GraphicsDeviceFeatures.IndependentBlend is false, then all members of BlendState.AttachmentStates must be equal.");
                    }
                }
            }
        }
        foreach (VertexLayoutDescription layoutDesc in description.ShaderSet.VertexLayouts)
        {
            bool hasExplicitLayout = false;
            uint minOffset = 0;
            foreach (VertexElementDescription elementDesc in layoutDesc.Elements)
            {
                if (hasExplicitLayout && elementDesc.Offset == 0)
                {
                    throw new NeoVeldridException(
                        $"If any vertex element has an explicit offset, then all elements must have an explicit offset.");
                }

                if (elementDesc.Offset != 0 && elementDesc.Offset < minOffset)
                {
                    throw new NeoVeldridException(
                        $"Vertex element \"{elementDesc.Name}\" has an explicit offset which overlaps with the previous element.");
                }

                minOffset = elementDesc.Offset + FormatSizeHelpers.GetSizeInBytes(elementDesc.Format);
                hasExplicitLayout |= elementDesc.Offset != 0;
            }

            if (minOffset > layoutDesc.Stride)
            {
                throw new NeoVeldridException(
                    $"The vertex layout's stride ({layoutDesc.Stride}) is less than the full size of the vertex ({minOffset})");
            }
        }
#endif
        return CreateGraphicsPipelineCore(ref description);
    }

    /// <summary></summary>
    /// <param name="description"></param>
    /// <returns></returns>
    protected abstract Pipeline CreateGraphicsPipelineCore(ref GraphicsPipelineDescription description);

    /// <summary>
    /// Creates a new compute <see cref="Pipeline"/> object.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Pipeline"/> which, when bound to a CommandList, is used to dispatch compute commands.</returns>
    public Pipeline CreateComputePipeline(ComputePipelineDescription description) => CreateComputePipeline(ref description);

    /// <summary>
    /// Creates a new compute <see cref="Pipeline"/> object.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Pipeline"/> which, when bound to a CommandList, is used to dispatch compute commands.</returns>
    public abstract Pipeline CreateComputePipeline(ref ComputePipelineDescription description);

    /// <summary>
    /// Creates a new <see cref="Framebuffer"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Framebuffer"/>.</returns>
    public Framebuffer CreateFramebuffer(FramebufferDescription description) => CreateFramebuffer(ref description);
    /// <summary>
    /// Creates a new <see cref="Framebuffer"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Framebuffer"/>.</returns>
    public abstract Framebuffer CreateFramebuffer(ref FramebufferDescription description);

    /// <summary>
    /// Creates a new <see cref="Texture"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Texture"/>.</returns>
    public Texture CreateTexture(TextureDescription description) => CreateTexture(ref description);
    /// <summary>
    /// Creates a new <see cref="Texture"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Texture"/>.</returns>
    public Texture CreateTexture(ref TextureDescription description)
    {
        using GraphicsDevice.TextureOperationScope operation =
            GraphicsDevice.AcquireTextureOperation();
        ValidateTextureSupport(description, operation.GetSupport(description));
        return CreateTextureCore(ref description);
    }

    /// <summary>
    /// Creates a new <see cref="Texture"/> from an existing native texture.
    /// </summary>
    /// <param name="nativeTexture">A backend-specific handle identifying an existing native texture. See remarks.</param>
    /// <param name="description">The properties of the existing Texture.</param>
    /// <returns>A new <see cref="Texture"/> wrapping the existing native texture.</returns>
    /// <remarks>
    /// The nativeTexture parameter is backend-specific, and the type of data passed in depends on which graphics API is
    /// being used.
    /// The Vulkan backend rejects this legacy overload because a VkImage handle alone cannot declare ownership, initial
    /// layout, queue-family, creation flags, and subresource metadata.
    /// When using the D3D11 backend, nativeTexture must be a valid pointer to an ID3D11Texture2D whose native descriptor
    /// exactly matches the descriptor NeoVeldrid creates for the supplied description. Typed resources and resources with
    /// additional bind or miscellaneous flags are intentionally rejected because this overload cannot faithfully expose
    /// their additional metadata.
    /// When using the OpenGL backend, nativeTexture must be a valid OpenGL texture name.
    /// The properties of the Texture will be determined from the <see cref="TextureDescription"/> passed in. These
    /// properties must match the true properties of the existing native texture. The description must also represent a
    /// texture which this factory's <see cref="GraphicsDevice"/> could create and consume itself. This overload is not a
    /// general native-resource import contract for images requiring backend-specific ownership, layout, or creation metadata.
    /// </remarks>
    public Texture CreateTexture(ulong nativeTexture, TextureDescription description) =>
        CreateTexture(nativeTexture, ref description);

    private void ValidateTextureSupport(
        in TextureDescription description,
        TextureSupportResult result)
    {
        if (!result.IsSupported)
        {
            throw new TextureNotSupportedException(
                GraphicsDevice.BackendType,
                description,
                result);
        }
    }

    /// <summary>
    /// Creates a new <see cref="Texture"/> from an existing native texture.
    /// </summary>
    /// <param name="nativeTexture">A backend-specific handle identifying an existing native texture. See remarks.</param>
    /// <param name="description">The properties of the existing Texture.</param>
    /// <returns>A new <see cref="Texture"/> wrapping the existing native texture.</returns>
    /// <remarks>
    /// The nativeTexture parameter is backend-specific, and the type of data passed in depends on which graphics API is
    /// being used.
    /// The Vulkan backend rejects this legacy overload because a VkImage handle alone cannot declare ownership, initial
    /// layout, queue-family, creation flags, and subresource metadata.
    /// When using the D3D11 backend, nativeTexture must be a valid pointer to an ID3D11Texture2D whose native descriptor
    /// exactly matches the descriptor NeoVeldrid creates for the supplied description. Typed resources and resources with
    /// additional bind or miscellaneous flags are intentionally rejected because this overload cannot faithfully expose
    /// their additional metadata.
    /// When using the OpenGL backend, nativeTexture must be a valid OpenGL texture name.
    /// The properties of the Texture will be determined from the <see cref="TextureDescription"/> passed in. These
    /// properties must match the true properties of the existing native texture. The description must also represent a
    /// texture which this factory's <see cref="GraphicsDevice"/> could create and consume itself. This overload is not a
    /// general native-resource import contract for images requiring backend-specific ownership, layout, or creation metadata.
    /// </remarks>
    public Texture CreateTexture(ulong nativeTexture, ref TextureDescription description)
    {
        using GraphicsDevice.TextureOperationScope operation =
            GraphicsDevice.AcquireTextureOperation();
        ValidateTextureSupport(description, operation.GetSupport(description));
        if (!SupportsNativeTextureImport(description))
        {
            throw new TextureNotSupportedException(
                GraphicsDevice.BackendType,
                description,
                TextureSupportResult.Unsupported(
                    TextureSupportClassification.BackendContract,
                    TextureSupportReason.NativeTextureImport));
        }

        ValidateNativeTextureImport(nativeTexture, description);
        return CreateTextureCore(nativeTexture, ref description);
    }

    /// <summary>
    /// Gets whether this backend's native-texture wrapper can faithfully represent the complete validated description.
    /// Ordinary texture support does not imply that a backend can import the ownership and metadata of an arbitrary
    /// externally-created image.
    /// </summary>
    /// <param name="description">The otherwise-supported texture description proposed for native import.</param>
    /// <returns>True when the backend's native wrapper implements the description; otherwise false.</returns>
    protected virtual bool SupportsNativeTextureImport(
        in TextureDescription description) => true;

    /// <summary>
    /// Forwards the native-import shape contract through a factory decorator.
    /// </summary>
    protected static bool GetNativeTextureImportSupport(
        ResourceFactory factory,
        in TextureDescription description)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.SupportsNativeTextureImport(description);
    }

    /// <summary>
    /// Validates the backend-specific handle and its relationship to an otherwise-supported texture description.
    /// Backends should reject handles which cannot be faithfully represented by the public <see cref="Texture"/>
    /// contract instead of deferring the failure to native resource use.
    /// </summary>
    /// <param name="nativeTexture">The backend-specific native texture handle.</param>
    /// <param name="description">The validated description proposed for the imported texture.</param>
    protected virtual void ValidateNativeTextureImport(
        ulong nativeTexture,
        in TextureDescription description)
    {
        if (nativeTexture == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nativeTexture),
                nativeTexture,
                "A native texture handle must be non-zero.");
        }
    }

    /// <summary>
    /// Forwards backend-specific native-handle validation through a factory decorator.
    /// </summary>
    protected static void ValidateNativeTextureImport(
        ResourceFactory factory,
        ulong nativeTexture,
        in TextureDescription description)
    {
        ArgumentNullException.ThrowIfNull(factory);
        factory.ValidateNativeTextureImport(nativeTexture, description);
    }

    /// <summary></summary>
    /// <param name="nativeTexture"></param>
    /// <param name="description"></param>
    /// <returns></returns>
    protected abstract Texture CreateTextureCore(ulong nativeTexture, ref TextureDescription description);

    /// <summary>
    /// </summary>
    /// <param name="description"></param>
    /// <returns></returns>
    protected abstract Texture CreateTextureCore(ref TextureDescription description);

    /// <summary>
    /// Creates a new <see cref="TextureView"/>.
    /// </summary>
    /// <param name="target">The target <see cref="Texture"/> used in the new view.</param>
    /// <returns>A new <see cref="TextureView"/>.</returns>
    public TextureView CreateTextureView(Texture target) => CreateTextureView(new TextureViewDescription(target));
    /// <summary>
    /// Creates a new <see cref="TextureView"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="TextureView"/>.</returns>
    public TextureView CreateTextureView(TextureViewDescription description) => CreateTextureView(ref description);
    /// <summary>
    /// Creates a new <see cref="TextureView"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="TextureView"/>.</returns>
    public TextureView CreateTextureView(ref TextureViewDescription description)
    {
#if VALIDATE_USAGE
        if (description.MipLevels == 0 || description.ArrayLayers == 0
            || (description.BaseMipLevel + description.MipLevels) > description.Target.MipLevels
            || (description.BaseArrayLayer + description.ArrayLayers) > description.Target.ArrayLayers)
        {
            throw new NeoVeldridException(
                "TextureView mip level and array layer range must be contained in the target Texture.");
        }
        if ((description.Target.Usage & TextureUsage.Sampled) == 0
            && (description.Target.Usage & TextureUsage.Storage) == 0)
        {
            throw new NeoVeldridException(
                "To create a TextureView, the target texture must have either Sampled or Storage usage flags.");
        }
        if (!Features.SubsetTextureView &&
            (description.BaseMipLevel != 0 || description.MipLevels != description.Target.MipLevels
            || description.BaseArrayLayer != 0 || description.ArrayLayers != description.Target.ArrayLayers))
        {
            throw new NeoVeldridException("GraphicsDevice does not support subset TextureViews.");
        }
        if (description.Format != null && description.Format != description.Target.Format)
        {
            if (!FormatHelpers.IsFormatViewCompatible(description.Format.Value, description.Target.Format))
            {
                throw new NeoVeldridException(
                    $"Cannot create a TextureView with format {description.Format.Value} targeting a Texture with format " +
                    $"{description.Target.Format}. A TextureView's format must have the same size and number of " +
                    $"components as the underlying Texture's format, or the same format.");
            }
        }
#endif

        return CreateTextureViewCore(ref description);
    }

    /// <summary>
    /// </summary>
    /// <param name="description"></param>
    /// <returns></returns>
    protected abstract TextureView CreateTextureViewCore(ref TextureViewDescription description);

    /// <summary>
    /// Creates a new <see cref="DeviceBuffer"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="DeviceBuffer"/>.</returns>
    public DeviceBuffer CreateBuffer(BufferDescription description) => CreateBuffer(ref description);

    /// <summary>
    /// Creates a new <see cref="DeviceBuffer"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="DeviceBuffer"/>.</returns>
    public DeviceBuffer CreateBuffer(ref BufferDescription description)
    {
#if VALIDATE_USAGE
        BufferUsage usage = description.Usage;
        if ((usage & BufferUsage.StructuredBufferReadOnly) == BufferUsage.StructuredBufferReadOnly
            || (usage & BufferUsage.StructuredBufferReadWrite) == BufferUsage.StructuredBufferReadWrite)
        {
            if (!Features.StructuredBuffer)
            {
                throw new NeoVeldridException("GraphicsDevice does not support structured buffers.");
            }

            if (description.StructureByteStride == 0)
            {
                throw new NeoVeldridException("Structured Buffer objects must have a non-zero StructureByteStride.");
            }

            if ((usage & BufferUsage.UniformBuffer) != 0)
            {
                throw new NeoVeldridException(
                    $"Structured Buffer objects cannot specify {nameof(BufferUsage)}.{nameof(BufferUsage.UniformBuffer)}.");
            }
            if (description.UseTypedHlslBinding
                && (usage & (BufferUsage.VertexBuffer | BufferUsage.IndexBuffer | BufferUsage.IndirectBuffer)) != 0)
            {
                throw new NeoVeldridException(
                    $"A structured buffer with {nameof(BufferDescription.UseTypedHlslBinding)} set cannot also specify {nameof(BufferUsage.VertexBuffer)}, {nameof(BufferUsage.IndexBuffer)}, or {nameof(BufferUsage.IndirectBuffer)}. Leave {nameof(BufferDescription.UseTypedHlslBinding)} false (the default) to fill a vertex, index, or indirect buffer from a compute shader.");
            }
        }
        else if (description.StructureByteStride != 0)
        {
            throw new NeoVeldridException("Non-structured Buffers must have a StructureByteStride of zero.");
        }
        if ((usage & BufferUsage.Staging) != 0 && usage != BufferUsage.Staging)
        {
            throw new NeoVeldridException("Buffers with Staging Usage must not specify any other Usage flags.");
        }
        if ((usage & BufferUsage.Dynamic) != 0
            && (usage & (BufferUsage.StructuredBufferReadWrite | BufferUsage.IndirectBuffer)) != 0)
        {
            throw new NeoVeldridException(
                $"{nameof(BufferUsage)}.{nameof(BufferUsage.Dynamic)} cannot be combined with {nameof(BufferUsage.StructuredBufferReadWrite)} or {nameof(BufferUsage.IndirectBuffer)}.");
        }
        if ((usage & BufferUsage.UniformBuffer) != 0 && (description.SizeInBytes % 16) != 0)
        {
            throw new NeoVeldridException($"Uniform buffer size must be a multiple of 16 bytes.");
        }
#endif
        return CreateBufferCore(ref description);
    }

    /// <summary>
    /// </summary>
    /// <param name="description"></param>
    /// <returns></returns>
    protected abstract DeviceBuffer CreateBufferCore(ref BufferDescription description);

    /// <summary>
    /// Creates a new <see cref="Sampler"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Sampler"/>.</returns>
    public Sampler CreateSampler(SamplerDescription description) => CreateSampler(ref description);
    /// <summary>
    /// Creates a new <see cref="Sampler"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Sampler"/>.</returns>
    public Sampler CreateSampler(ref SamplerDescription description)
    {
#if VALIDATE_USAGE
        if (!Features.SamplerLodBias && description.LodBias != 0)
        {
            throw new NeoVeldridException(
                "GraphicsDevice does not support Sampler LOD bias. SamplerDescription.LodBias must be 0.");
        }
        if (!Features.SamplerAnisotropy && description.Filter == SamplerFilter.Anisotropic)
        {
            throw new NeoVeldridException(
                "SamplerFilter.Anisotropic cannot be used unless GraphicsDeviceFeatures.SamplerAnisotropy is supported.");
        }
#endif

        return CreateSamplerCore(ref description);
    }

    /// <summary></summary>
    /// <param name="description"></param>
    /// <returns></returns>
    protected abstract Sampler CreateSamplerCore(ref SamplerDescription description);

    /// <summary>
    /// Creates a new <see cref="Shader"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Shader"/>.</returns>
    public Shader CreateShader(ShaderDescription description) => CreateShader(ref description);
    /// <summary>
    /// Creates a new <see cref="Shader"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Shader"/>.</returns>
    public Shader CreateShader(ref ShaderDescription description)
    {
#if VALIDATE_USAGE
        if (!Features.ComputeShader && description.Stage == ShaderStages.Compute)
        {
            throw new NeoVeldridException("GraphicsDevice does not support Compute Shaders.");
        }
        if (!Features.GeometryShader && description.Stage == ShaderStages.Geometry)
        {
            throw new NeoVeldridException("GraphicsDevice does not support Compute Shaders.");
        }
        if (!Features.TessellationShaders
            && (description.Stage == ShaderStages.TessellationControl
                || description.Stage == ShaderStages.TessellationEvaluation))
        {
            throw new NeoVeldridException("GraphicsDevice does not support Tessellation Shaders.");
        }
#endif
        return CreateShaderCore(ref description);
    }

    /// <summary></summary>
    /// <param name="description"></param>
    /// <returns></returns>
    protected abstract Shader CreateShaderCore(ref ShaderDescription description);

    /// <summary>
    /// Creates a new <see cref="CommandList"/>.
    /// </summary>
    /// <returns>A new <see cref="CommandList"/>.</returns>
    public CommandList CreateCommandList() => CreateCommandList(new CommandListDescription());
    /// <summary>
    /// Creates a new <see cref="CommandList"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="CommandList"/>.</returns>
    public CommandList CreateCommandList(CommandListDescription description) => CreateCommandList(ref description);
    /// <summary>
    /// Creates a new <see cref="CommandList"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="CommandList"/>.</returns>
    public abstract CommandList CreateCommandList(ref CommandListDescription description);

    /// <summary>
    /// Creates a new <see cref="ResourceLayout"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="ResourceLayout"/>.</returns>
    public ResourceLayout CreateResourceLayout(ResourceLayoutDescription description) => CreateResourceLayout(ref description);
    /// <summary>
    /// Creates a new <see cref="ResourceLayout"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="ResourceLayout"/>.</returns>
    public abstract ResourceLayout CreateResourceLayout(ref ResourceLayoutDescription description);

    /// <summary>
    /// Creates a new <see cref="ResourceSet"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="ResourceSet"/>.</returns>
    public ResourceSet CreateResourceSet(ResourceSetDescription description) => CreateResourceSet(ref description);
    /// <summary>
    /// Creates a new <see cref="ResourceSet"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="ResourceSet"/>.</returns>
    public abstract ResourceSet CreateResourceSet(ref ResourceSetDescription description);

    /// <summary>
    /// Creates a new <see cref="Fence"/> in the given state.
    /// </summary>
    /// <param name="signaled">A value indicating whether the Fence should be in the signaled state when created.</param>
    /// <returns>A new <see cref="Fence"/>.</returns>
    public abstract Fence CreateFence(bool signaled);

    /// <summary>
    /// Creates a new <see cref="Swapchain"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Swapchain"/>.</returns>
    public Swapchain CreateSwapchain(SwapchainDescription description) => CreateSwapchain(ref description);
    /// <summary>
    /// Creates a new <see cref="Swapchain"/>.
    /// </summary>
    /// <param name="description">The desired properties of the created object.</param>
    /// <returns>A new <see cref="Swapchain"/>.</returns>
    public abstract Swapchain CreateSwapchain(ref SwapchainDescription description);
}
