using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Core.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace NeoVeldrid.D3D11;

internal unsafe class D3D11GraphicsDevice : GraphicsDevice
{
    /// <summary>DXGI_DEBUG_ALL - reports live objects from all producers (D3D11, DXGI, etc.).</summary>
    private static readonly Guid DxgiDebugAll = new Guid("e48ae283-da80-490b-87e6-43e9a9cfda08");

    private ComPtr<IDXGIAdapter> _dxgiAdapter;
    private ComPtr<ID3D11Device> _device;
    private readonly string _deviceName;
    private readonly string _vendorName;
    private readonly GraphicsApiVersion _apiVersion;
    private readonly int _deviceId;
    private ComPtr<ID3D11DeviceContext> _immediateContext;
    private readonly D3D11ResourceFactory _d3d11ResourceFactory;
    private readonly D3D11Swapchain _mainSwapchain;
    private readonly bool _supportsConcurrentResources;
    private readonly D3D11CommandListCapabilities _commandListCapabilities;
    private readonly int? _debugLayerProbeHResult;
    private readonly bool _debugDeviceCreated;
    private D3D11ValidationMessageQueue _validationMessages;
    private readonly object _immediateContextLock = new object();
    private readonly BackendInfoD3D11 _d3d11Info;

    private readonly object _mappedResourceLock = new object();
    private readonly Dictionary<MappedResourceCacheKey, MappedResourceInfo> _mappedResources
        = new Dictionary<MappedResourceCacheKey, MappedResourceInfo>();

    private readonly object _stagingResourcesLock = new object();
    private readonly List<D3D11Buffer> _availableStagingBuffers = new List<D3D11Buffer>();

    private readonly Silk.NET.Direct3D11.D3D11 _d3d11Api;

    public override string DeviceName => _deviceName;

    public override string VendorName => _vendorName;

    public override GraphicsApiVersion ApiVersion => _apiVersion;

    public override GraphicsBackend BackendType => GraphicsBackend.Direct3D11;

    public override bool IsUvOriginTopLeft => true;

    public override bool IsDepthRangeZeroToOne => true;

    public override bool IsClipSpaceYInverted => false;

    public override ResourceFactory ResourceFactory => _d3d11ResourceFactory;

    public ID3D11Device* Device => _device;

    public IDXGIAdapter* Adapter => _dxgiAdapter;

    public bool SupportsConcurrentResources => _supportsConcurrentResources;

    public D3D11CommandListCapabilities CommandListCapabilities =>
        _commandListCapabilities;

    public int DeviceId => _deviceId;

    internal int? DebugLayerProbeHResult => _debugLayerProbeHResult;

    internal bool DebugDeviceCreated => _debugDeviceCreated;

    internal bool ValidationInfoQueueActivated => _validationMessages != null;

    internal ulong CollectedValidationMessageCount =>
        _validationMessages?.CollectedMessageCount ?? 0;

    internal ulong DiscardedValidationMessageCount =>
        _validationMessages?.DiscardedMessageCount ?? 0;

    public override Swapchain MainSwapchain => _mainSwapchain;

    public override GraphicsDeviceFeatures Features { get; }

    public D3D11GraphicsDevice(GraphicsDeviceOptions options, D3D11DeviceOptions d3D11DeviceOptions, SwapchainDescription? swapchainDesc)
        : this(MergeOptions(d3D11DeviceOptions, options), swapchainDesc)
    {
    }

    public D3D11GraphicsDevice(D3D11DeviceOptions options, SwapchainDescription? swapchainDesc)
    {
        try
        {
            var flags = (CreateDeviceFlag)options.DeviceCreationFlags;
            bool debugRequested = (flags & CreateDeviceFlag.Debug) != 0;
            InitializeValidation(GraphicsBackend.Direct3D11, debugRequested);

#pragma warning disable CS0618
            _d3d11Api = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Silk.NET.Direct3D11.D3D11.GetApi(DXSwapchainProvider.Win32)
                : Silk.NET.Direct3D11.D3D11.GetApi(DXSwapchainProvider.Sdl2);
#pragma warning restore CS0618

            // If debug flag set but SDK layers aren't available we can't enable debug.
            if ((flags & CreateDeviceFlag.Debug) != 0)
            {
                int probeResult = ProbeSdkLayers(_d3d11Api);
                _debugLayerProbeHResult = probeResult;
                if (probeResult < 0)
                {
                    flags &= ~CreateDeviceFlag.Debug;
                }
            }

            D3DFeatureLevel featureLevel;
            ID3D11Device* pDevice = null;
            ID3D11DeviceContext* pContext = null;
            IDXGIAdapter* pAdapter = options.AdapterPtr != IntPtr.Zero
                ? (IDXGIAdapter*)options.AdapterPtr
                : null;
            D3DDriverType driverType = pAdapter != null
                ? D3DDriverType.Unknown
                : D3DDriverType.Hardware;

            try
            {
                try
                {
                    D3DFeatureLevel* pFeatureLevels = stackalloc D3DFeatureLevel[]
                    {
                        D3DFeatureLevel.Level111,
                        D3DFeatureLevel.Level110,
                    };

                    SilkMarshal.ThrowHResult(_d3d11Api.CreateDevice(
                        pAdapter,
                        driverType,
                        IntPtr.Zero,
                        (uint)flags,
                        pFeatureLevels,
                        2,
                        Silk.NET.Direct3D11.D3D11.SdkVersion,
                        &pDevice,
                        &featureLevel,
                        &pContext));
                }
                catch
                {
                    // A failed COM call is allowed to have populated its output
                    // pointers. Release any such partial result before retrying.
                    if (pContext != null)
                    {
                        pContext->Release();
                        pContext = null;
                    }
                    if (pDevice != null)
                    {
                        pDevice->Release();
                        pDevice = null;
                    }

                    // Windows 7's D3D11 runtime rejects a feature-level list
                    // containing 11.1 with E_INVALIDARG even when the adapter
                    // supports 11.0. Retry the same adapter at NeoVeldrid's
                    // explicit 11.0 minimum instead of letting the driver
                    // silently select a 10.x level.
                    D3DFeatureLevel minimumFeatureLevel =
                        D3DFeatureLevel.Level110;
                    SilkMarshal.ThrowHResult(_d3d11Api.CreateDevice(
                        pAdapter,
                        driverType,
                        IntPtr.Zero,
                        (uint)flags,
                        &minimumFeatureLevel,
                        1,
                        Silk.NET.Direct3D11.D3D11.SdkVersion,
                        &pDevice,
                        &featureLevel,
                        &pContext));
                }

                EnsureSupportedFeatureLevel(featureLevel);

                // Transfer both successful creation references into fields before
                // any subsequent operation can throw. The finally block owns only
                // pointers which were not transferred.
                _device = default;
                _device.Handle = pDevice;
                pDevice = null;
                _immediateContext = default;
                _immediateContext.Handle = pContext;
                pContext = null;
            }
            finally
            {
                if (pContext != null)
                {
                    pContext->Release();
                }
                if (pDevice != null)
                {
                    pDevice->Release();
                }
            }

            _debugDeviceCreated = (flags & CreateDeviceFlag.Debug) != 0;
            ActivateValidationMessageQueue();

            // Query adapter information
            {
                IDXGIDevice* pDxgiDevice = null;
                try
                {
                    Guid dxgiDeviceGuid = IDXGIDevice.Guid;
                    SilkMarshal.ThrowHResult(
                        ((IUnknown*)_device.Handle)->QueryInterface(
                            &dxgiDeviceGuid,
                            (void**)&pDxgiDevice));

                    IDXGIAdapter* pAdapterOut = null;
                    try
                    {
                        SilkMarshal.ThrowHResult(pDxgiDevice->GetAdapter(&pAdapterOut));

                        // GetAdapter returns an owned reference. Transfer it before
                        // reading metadata so constructor cleanup can always see it.
                        _dxgiAdapter = default;
                        _dxgiAdapter.Handle = pAdapterOut;
                        pAdapterOut = null;

                        AdapterDesc desc;
                        SilkMarshal.ThrowHResult(_dxgiAdapter.Handle->GetDesc(&desc));
                        _deviceName = new string(desc.Description);
                        _vendorName = "id:" + desc.VendorId.ToString("x8");
                        _deviceId = (int)desc.DeviceId;
                    }
                    finally
                    {
                        if (pAdapterOut != null)
                        {
                            pAdapterOut->Release();
                        }
                    }
                }
                finally
                {
                    if (pDxgiDevice != null)
                    {
                        pDxgiDevice->Release();
                    }
                }
            }

            switch (featureLevel)
            {
                case D3DFeatureLevel.Level110:
                    _apiVersion = new GraphicsApiVersion(11, 0, 0, 0);
                    break;

                case D3DFeatureLevel.Level111:
                    _apiVersion = new GraphicsApiVersion(11, 1, 0, 0);
                    break;

                case D3DFeatureLevel.Level120:
                    _apiVersion = new GraphicsApiVersion(12, 0, 0, 0);
                    break;

                case D3DFeatureLevel.Level121:
                    _apiVersion = new GraphicsApiVersion(12, 1, 0, 0);
                    break;

                case D3DFeatureLevel.Level122:
                    _apiVersion = new GraphicsApiVersion(12, 2, 0, 0);
                    break;
            }

            if (swapchainDesc != null)
            {
                SwapchainDescription desc = swapchainDesc.Value;
                _mainSwapchain = new D3D11Swapchain(this, ref desc);
            }

            // Check threading support
            FeatureDataThreading threadingData = default;
            SilkMarshal.ThrowHResult(
                _device.Handle->CheckFeatureSupport(
                    Silk.NET.Direct3D11.Feature.Threading,
                    &threadingData,
                    (uint)sizeof(FeatureDataThreading)));
            _supportsConcurrentResources = threadingData.DriverConcurrentCreates;
            _commandListCapabilities = new D3D11CommandListCapabilities(
                threadingData.DriverCommandLists,
                options.DeferredTextureUploadMode);

            // Check double precision support
            FeatureDataDoubles doublesData = default;
            SilkMarshal.ThrowHResult(
                _device.Handle->CheckFeatureSupport(
                    Silk.NET.Direct3D11.Feature.Doubles,
                    &doublesData,
                    (uint)sizeof(FeatureDataDoubles)));

            Features = new GraphicsDeviceFeatures(
                computeShader: true,
                geometryShader: true,
                tessellationShaders: true,
                multipleViewports: true,
                samplerLodBias: true,
                drawBaseVertex: true,
                drawBaseInstance: true,
                drawIndirect: true,
                drawIndirectBaseInstance: true,
                fillModeWireframe: true,
                samplerAnisotropy: true,
                depthClipDisable: true,
                texture1D: true,
                independentBlend: true,
                structuredBuffer: featureLevel >= D3DFeatureLevel.Level110,
                subsetTextureView: true,
                commandListDebugMarkers: featureLevel >= D3DFeatureLevel.Level111,
                bufferRangeBinding: featureLevel >= D3DFeatureLevel.Level111,
                shaderFloat64: doublesData.DoublePrecisionFloatShaderOps);

            _d3d11ResourceFactory = new D3D11ResourceFactory(this);
            _d3d11Info = new BackendInfoD3D11(this);

            CompleteDeviceCreation();
        }
        catch (Exception creationError)
        {
            FailDeviceCreation(creationError);
        }
    }

    private void ActivateValidationMessageQueue()
    {
        if (_debugDeviceCreated)
        {
            if (D3D11ValidationMessageQueue.TryCreate(
                _device.Handle,
                out D3D11ValidationMessageQueue validationMessages,
                out string activationFailure))
            {
                _validationMessages = validationMessages;

                if (_validationMessages.CreationBaselineDiscardedMessageCount != 0)
                {
                    Validation.Report(
                        GraphicsDeviceValidationSeverity.Error,
                        "D3D11",
                        "InfoQueue",
                        "CreationBaselineMessagesDiscarded",
                        $"The D3D11 info queue reported "
                        + $"{_validationMessages.CreationBaselineDiscardedMessageCount} message(s) "
                        + "discarded by its capacity limit before NeoVeldrid could activate the queue "
                        + "immediately after D3D11CreateDevice returned. Their contents are unavailable.");
                }

                if (_validationMessages.CreationBaselineStorageDeniedMessageCount != 0)
                {
                    Validation.Report(
                        GraphicsDeviceValidationSeverity.Warning,
                        "D3D11",
                        "InfoQueue",
                        "CreationBaselineMessagesDeniedByStorageFilter",
                        $"The D3D11 info queue reported "
                        + $"{_validationMessages.CreationBaselineStorageDeniedMessageCount} message(s) "
                        + "denied by its initial storage filter before NeoVeldrid could access the queue. "
                        + "ID3D11InfoQueue is unavailable until D3D11CreateDevice returns; NeoVeldrid "
                        + "then acquired it immediately, cleared the filter, and retained this count as "
                        + "the creation baseline. Any subsequent increase fails validation.");
                }

                GraphicsDeviceValidationFeatures activeFeatures =
                    GraphicsDeviceValidationFeatures.ApiDebugOutput;
                if (_validationMessages.SupportsLiveObjectTracking)
                {
                    activeFeatures |= GraphicsDeviceValidationFeatures.LiveObjectTracking;
                }

                Validation.SetActive(activeFeatures, "ID3D11InfoQueue");
            }
            else
            {
                Validation.SetInactive(activationFailure);
            }
        }
        else if (_debugLayerProbeHResult is int probeResult && probeResult < 0)
        {
            Validation.SetInactive(
                $"The D3D11 SDK layers were unavailable (HRESULT 0x{probeResult:X8}); "
                + "the device was created without the debug layer.");
        }
        else
        {
            Validation.SetInactive("The Direct3D 11 debug layer was not requested.");
        }
    }

    private static int ProbeSdkLayers(Silk.NET.Direct3D11.D3D11 d3d11)
    {
        // Try creating a null device with debug flag to check if SDK layers are installed
        return d3d11.CreateDevice(
            (IDXGIAdapter*)null,
            D3DDriverType.Null,
            IntPtr.Zero,
            (uint)CreateDeviceFlag.Debug,
            (D3DFeatureLevel*)null,
            0,
            Silk.NET.Direct3D11.D3D11.SdkVersion,
            (ID3D11Device**)null,
            (D3DFeatureLevel*)null,
            (ID3D11DeviceContext**)null);
    }

    private static D3D11DeviceOptions MergeOptions(D3D11DeviceOptions d3D11DeviceOptions, GraphicsDeviceOptions options)
    {
        if (options.Debug)
        {
            d3D11DeviceOptions.DeviceCreationFlags |= (uint)CreateDeviceFlag.Debug;
        }

        return d3D11DeviceOptions;
    }

    internal static GraphicsApiVersion GetApiVersion()
    {
        GraphicsApiVersion result = GraphicsApiVersion.Unknown;

        using (var d3d11 = Silk.NET.Direct3D11.D3D11.GetApi(null))
        {
            D3DFeatureLevel[] featureLevels = new[]
            {
                // Only checks for D3D11 capabilities.
                D3DFeatureLevel.Level111,
                D3DFeatureLevel.Level110,
            };

            ComPtr<ID3D11Device> device = default;
            ComPtr<ID3D11DeviceContext> context = default;
            D3DFeatureLevel maxLevel;

            unsafe
            {
                fixed (D3DFeatureLevel* pFeatureLevels = featureLevels)
                {
                    int hr = d3d11.CreateDevice(
                        default(ComPtr<IDXGIAdapter>),
                        D3DDriverType.Hardware,
                        IntPtr.Zero,
                        (uint)CreateDeviceFlag.None,
                        pFeatureLevels,
                        (uint)featureLevels.Length,
                        Silk.NET.Direct3D11.D3D11.SdkVersion,
                        ref device,
                        &maxLevel,
                        ref context);

                    if (hr >= 0)
                    {
                        // Avoids ToString() in case aggressive trimming strips
                        // enumerator value names. Thanks Microsoft for making
                        // D3DFeatureLevel values use a hexadecimal pattern!
                        int raw = (int)maxLevel;
                        int major = raw >> 12;
                        int minor = (raw >> 8) & 0xF;

                        result = new GraphicsApiVersion(major, minor, 0, 0);
                    }
                }
            }

            context.Dispose();
            device.Dispose();
        }

        return result;
    }

    private protected override void SubmitCommandsCore(CommandList cl, Fence fence)
    {
        D3D11CommandList d3d11CL = Util.AssertSubtype<CommandList, D3D11CommandList>(cl);
        D3D11Fence d3d11Fence = fence == null
            ? null
            : GetOwnedFence(fence);
        lock (_immediateContextLock)
        {
            ID3D11DeviceContext* context = _immediateContext.Handle;
            bool fenceReserved = false;
            if (d3d11Fence != null)
            {
                d3d11Fence.AcquireSubmission(this);
                fenceReserved = true;
            }

            try
            {
                bool executedCommandList = d3d11CL.DeviceCommandList != null;
                if (executedCommandList) // CommandList may have been reset in the meantime (resized swapchain).
                {
                    context->ExecuteCommandList(d3d11CL.DeviceCommandList, false);
                }

                if (d3d11Fence != null)
                {
                    // The reservation remains held from preflight through this
                    // marker, so Dispose cannot invalidate the query after the
                    // command list has been committed. Flush guarantees that an
                    // otherwise idle queue can make progress.
                    d3d11Fence.ArmReserved(context);
                    context->Flush();
                }

                // Complete managed ownership only after the optional native
                // completion marker has been committed.
                if (executedCommandList)
                {
                    d3d11CL.OnCompleted();
                }
            }
            finally
            {
                if (fenceReserved)
                {
                    d3d11Fence.ReleaseSubmission();
                }
            }
        }
    }

    private protected override void SwapBuffersCore(Swapchain swapchain)
    {
        lock (_immediateContextLock)
        {
            D3D11Swapchain d3d11SC = Util.AssertSubtype<Swapchain, D3D11Swapchain>(swapchain);
            SilkMarshal.ThrowHResult(
                d3d11SC.DxgiSwapChain->Present((uint)d3d11SC.SyncInterval, 0));
        }
    }

    public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
    {
        Format dxgiFormat = D3D11Formats.ToDxgiFormat(format, depthFormat);
        if (CheckFormatMultisample(dxgiFormat, 32))
        {
            return TextureSampleCount.Count32;
        }
        else if (CheckFormatMultisample(dxgiFormat, 16))
        {
            return TextureSampleCount.Count16;
        }
        else if (CheckFormatMultisample(dxgiFormat, 8))
        {
            return TextureSampleCount.Count8;
        }
        else if (CheckFormatMultisample(dxgiFormat, 4))
        {
            return TextureSampleCount.Count4;
        }
        else if (CheckFormatMultisample(dxgiFormat, 2))
        {
            return TextureSampleCount.Count2;
        }

        return TextureSampleCount.Count1;
    }

    private bool CheckFormatMultisample(Format format, int sampleCount)
    {
        uint numQualityLevels;
        int hr = ((ID3D11Device*)_device)->CheckMultisampleQualityLevels(format, (uint)sampleCount, &numQualityLevels);
        return hr >= 0 && numQualityLevels != 0;
    }

    internal static bool IsSupportedFeatureLevel(
        D3DFeatureLevel featureLevel) =>
        featureLevel >= D3DFeatureLevel.Level110;

    private static void EnsureSupportedFeatureLevel(
        D3DFeatureLevel featureLevel)
    {
        if (!IsSupportedFeatureLevel(featureLevel))
        {
            throw new NeoVeldridException(
                "The Direct3D 11 backend requires feature level 11.0 or newer, "
                + $"but D3D11CreateDevice selected 0x{(int)featureLevel:X4}.");
        }
    }

    private protected override TextureSupportResult GetTextureSupportCore(
        in TextureDescription description)
    {
        if (D3D11Formats.IsUnsupportedFormat(description.Format))
        {
            return UnsupportedTexture(
                TextureSupportClassification.BackendContract,
                TextureSupportReason.PixelFormat);
        }

        bool isSampled = (description.Usage & TextureUsage.Sampled) != 0;
        bool isStorage = (description.Usage & TextureUsage.Storage) != 0;
        bool isRenderTarget = (description.Usage & TextureUsage.RenderTarget) != 0;
        bool isDepthStencil = (description.Usage & TextureUsage.DepthStencil) != 0;
        bool isCubemap = (description.Usage & TextureUsage.Cubemap) != 0;
        bool isStaging = (description.Usage & TextureUsage.Staging) != 0;
        bool generatesMipmaps = (description.Usage & TextureUsage.GenerateMipmaps) != 0;

        // D3D11Framebuffer currently exposes only 2D and 2D-array RTV/DSV
        // dimensions. Report that backend contract instead of advertising the
        // broader set of dimensions supported by native D3D11 views.
        if (isRenderTarget && description.Type != TextureType.Texture2D)
        {
            return UnsupportedTexture(
                TextureSupportClassification.BackendContract,
                TextureSupportReason.RenderTargetUsage);
        }
        if (isDepthStencil
            && (description.Type != TextureType.Texture2D || isCubemap))
        {
            return UnsupportedTexture(
                TextureSupportClassification.BackendContract,
                TextureSupportReason.DepthStencilUsage);
        }

        // D3D11TextureView has no cubemap UAV representation. It also creates
        // an SRV for every storage view, so a storage texture must carry an
        // explicit or mip-generation-implied ShaderResource bind flag.
        if (isStorage && (isCubemap || (!isSampled && !generatesMipmaps)))
        {
            return UnsupportedTexture(
                TextureSupportClassification.BackendContract,
                TextureSupportReason.StorageUsage);
        }

        Format resourceFormat = D3D11Formats.GetTypelessFormat(
            D3D11Formats.ToDxgiFormat(description.Format, isDepthStencil));
        if (!TryGetFormatSupport(resourceFormat, out FormatSupport resourceSupport))
        {
            return UnsupportedTexture(
                TextureSupportClassification.DeviceCapability,
                TextureSupportReason.PixelFormat);
        }

        FormatSupport requiredTextureSupport = description.Type switch
        {
            TextureType.Texture1D => FormatSupport.Texture1D,
            TextureType.Texture2D => FormatSupport.Texture2D,
            TextureType.Texture3D => FormatSupport.Texture3D,
            _ => FormatSupport.None,
        };
        if ((resourceSupport & requiredTextureSupport) == 0)
        {
            return UnsupportedTexture(
                TextureSupportClassification.DeviceCapability,
                TextureSupportReason.TextureType);
        }

        Format shaderViewFormat = D3D11Formats.GetViewFormat(
            D3D11Formats.ToDxgiFormat(description.Format, isDepthStencil));
        if (!TryGetFormatSupport(shaderViewFormat, out FormatSupport shaderViewSupport))
        {
            return UnsupportedTexture(
                TextureSupportClassification.DeviceCapability,
                TextureSupportReason.PixelFormat);
        }

        // TextureUsage.Sampled means shader-resource access, which includes
        // integer Texture.Load operations. D3D11_FORMAT_SUPPORT_SHADER_SAMPLE
        // is a filtering capability and is not reported for formats such as
        // R32_UInt even though a typed SRV and ShaderLoad are fully supported.
        if (isSampled && (shaderViewSupport & FormatSupport.ShaderLoad) == 0)
        {
            return UnsupportedTexture(
                TextureSupportClassification.DeviceCapability,
                TextureSupportReason.SampledUsage);
        }
        if (isCubemap && (shaderViewSupport & FormatSupport.Texturecube) == 0)
        {
            return UnsupportedTexture(
                TextureSupportClassification.DeviceCapability,
                TextureSupportReason.CubemapUsage);
        }
        if (isStaging && (resourceSupport & FormatSupport.CpuLockable) == 0)
        {
            return UnsupportedTexture(
                TextureSupportClassification.DeviceCapability,
                TextureSupportReason.StagingUsage);
        }

        Format colorViewFormat = shaderViewFormat;
        FormatSupport colorViewSupport = shaderViewSupport;
        if (!isDepthStencil)
        {
            colorViewFormat = D3D11Formats.ToDxgiFormat(description.Format, false);
            if (!TryGetFormatSupport(colorViewFormat, out colorViewSupport))
            {
                return UnsupportedTexture(
                    TextureSupportClassification.DeviceCapability,
                    TextureSupportReason.PixelFormat);
            }
        }

        if (isRenderTarget && (colorViewSupport & FormatSupport.RenderTarget) == 0)
        {
            return UnsupportedTexture(
                TextureSupportClassification.DeviceCapability,
                TextureSupportReason.RenderTargetUsage);
        }

        FormatSupport depthViewSupport = FormatSupport.None;
        Format depthViewFormat = Format.FormatUnknown;
        if (isDepthStencil)
        {
            depthViewFormat = D3D11Formats.GetDepthFormat(description.Format);
            if (!TryGetFormatSupport(depthViewFormat, out depthViewSupport)
                || (depthViewSupport & FormatSupport.DepthStencil) == 0)
            {
                return UnsupportedTexture(
                    TextureSupportClassification.DeviceCapability,
                    TextureSupportReason.DepthStencilUsage);
            }
        }

        if (isStorage
            && ((shaderViewSupport & FormatSupport.TypedUnorderedAccessView) == 0
                || !SupportsTypedStorageAccess(shaderViewFormat)))
        {
            return UnsupportedTexture(
                TextureSupportClassification.DeviceCapability,
                TextureSupportReason.StorageUsage);
        }

        if (description.MipLevels > 1
            && (shaderViewSupport & FormatSupport.Mip) == 0)
        {
            return UnsupportedTexture(
                TextureSupportClassification.DeviceCapability,
                TextureSupportReason.MipLevelLimit);
        }

        if (generatesMipmaps)
        {
            const FormatSupport mipGenerationSupport =
                FormatSupport.ShaderSample
                | FormatSupport.Mip
                | FormatSupport.MipAutogen
                | FormatSupport.RenderTarget;
            if ((colorViewSupport & mipGenerationSupport) != mipGenerationSupport)
            {
                return UnsupportedTexture(
                    TextureSupportClassification.DeviceCapability,
                    TextureSupportReason.MipmapGeneration);
            }
        }

        GetTextureLimits(
            description.Type,
            isCubemap,
            out uint maxWidth,
            out uint maxHeight,
            out uint maxDepth,
            out uint maxMipLevels,
            out uint maxArrayLayers);
        if ((shaderViewSupport & FormatSupport.Mip) == 0)
            maxMipLevels = 1;

        uint sampleCounts = 1u << (int)TextureSampleCount.Count1;
        if (CanUseMultisampling(
            description,
            isSampled,
            isStorage,
            isRenderTarget,
            isDepthStencil,
            isCubemap,
            isStaging,
            generatesMipmaps,
            shaderViewSupport,
            colorViewSupport,
            depthViewSupport))
        {
            Format multisampleFormat = isDepthStencil
                ? depthViewFormat
                : colorViewFormat;
            if (CheckFormatMultisample(multisampleFormat, 2)) { sampleCounts |= 1u << (int)TextureSampleCount.Count2; }
            if (CheckFormatMultisample(multisampleFormat, 4)) { sampleCounts |= 1u << (int)TextureSampleCount.Count4; }
            if (CheckFormatMultisample(multisampleFormat, 8)) { sampleCounts |= 1u << (int)TextureSampleCount.Count8; }
            if (CheckFormatMultisample(multisampleFormat, 16)) { sampleCounts |= 1u << (int)TextureSampleCount.Count16; }
            if (CheckFormatMultisample(multisampleFormat, 32)) { sampleCounts |= 1u << (int)TextureSampleCount.Count32; }
        }

        return TextureSupportResult.Supported(new PixelFormatProperties(
            maxWidth,
            maxHeight,
            maxDepth,
            maxMipLevels,
            maxArrayLayers,
            sampleCounts));
    }

    private bool TryGetFormatSupport(Format format, out FormatSupport support)
    {
        uint rawSupport;
        int result = _device.Handle->CheckFormatSupport(format, &rawSupport);
        support = result >= 0 ? (FormatSupport)rawSupport : FormatSupport.None;
        return result >= 0;
    }

    private bool SupportsTypedStorageAccess(Format format)
    {
        FeatureDataFormatSupport2 support = new FeatureDataFormatSupport2
        {
            InFormat = format,
        };
        int result = _device.Handle->CheckFeatureSupport(
            Silk.NET.Direct3D11.Feature.FormatSupport2,
            &support,
            (uint)sizeof(FeatureDataFormatSupport2));
        const FormatSupport2 requiredSupport =
            FormatSupport2.UavTypedLoad | FormatSupport2.UavTypedStore;
        return result >= 0
            && (((FormatSupport2)support.OutFormatSupport2 & requiredSupport)
                == requiredSupport);
    }

    private static bool CanUseMultisampling(
        in TextureDescription description,
        bool isSampled,
        bool isStorage,
        bool isRenderTarget,
        bool isDepthStencil,
        bool isCubemap,
        bool isStaging,
        bool generatesMipmaps,
        FormatSupport shaderViewSupport,
        FormatSupport colorViewSupport,
        FormatSupport depthViewSupport)
    {
        if (description.Type != TextureType.Texture2D
            || isStorage
            || isCubemap
            || isStaging
            || generatesMipmaps)
        {
            return false;
        }

        if (isSampled
            && (shaderViewSupport & FormatSupport.MultisampleLoad) == 0)
        {
            return false;
        }

        FormatSupport targetSupport = isDepthStencil
            ? depthViewSupport
            : colorViewSupport;
        if ((isRenderTarget || isDepthStencil)
            && (targetSupport & FormatSupport.MultisampleRendertarget) == 0)
        {
            return false;
        }

        // NeoVeldrid permits every multisampled color texture to be passed to
        // ResolveTexture, regardless of its other usage flags.
        return isDepthStencil
            || (colorViewSupport & FormatSupport.MultisampleResolve) != 0;
    }

    private static void GetTextureLimits(
        TextureType type,
        bool isCubemap,
        out uint maxWidth,
        out uint maxHeight,
        out uint maxDepth,
        out uint maxMipLevels,
        out uint maxArrayLayers)
    {
        const uint MaxTextureDimension = 16384;
        const uint MaxVolumeExtent = 2048;
        const uint MaxTextureArrayLayers = 2048;

        if (type == TextureType.Texture1D)
        {
            maxWidth = MaxTextureDimension;
            maxHeight = 1;
            maxDepth = 1;
            maxMipLevels = 15;
            maxArrayLayers = MaxTextureArrayLayers;
        }
        else if (type == TextureType.Texture2D)
        {
            maxWidth = MaxTextureDimension;
            maxHeight = MaxTextureDimension;
            maxDepth = 1;
            maxMipLevels = 15;
            // Native cube arrays contain six physical slices per public,
            // logical cube. D3D11's largest valid multiple of six is 2046.
            maxArrayLayers = isCubemap ? 341u : MaxTextureArrayLayers;
        }
        else
        {
            maxWidth = MaxVolumeExtent;
            maxHeight = MaxVolumeExtent;
            maxDepth = MaxVolumeExtent;
            maxMipLevels = 12;
            maxArrayLayers = 1;
        }
    }

    private static TextureSupportResult UnsupportedTexture(
        TextureSupportClassification classification,
        TextureSupportReason reason) =>
        TextureSupportResult.Unsupported(classification, reason);

    protected override MappedResource MapCore(MappableResource resource, MapMode mode, uint subresource)
    {
        MappedResourceCacheKey key = new MappedResourceCacheKey(resource, subresource);
        lock (_mappedResourceLock)
        {
            if (_mappedResources.TryGetValue(key, out MappedResourceInfo info))
            {
                if (info.Mode != mode)
                {
                    throw NeoVeldridMappedResourceException.ConflictingMode(resource, subresource);
                }

                info.RefCount += 1;
                _mappedResources[key] = info;
            }
            else
            {
                // No current mapping exists -- create one.

                if (resource is D3D11Buffer buffer)
                {
                    lock (_immediateContextLock)
                    {
                        MappedSubresource msr;
                        SilkMarshal.ThrowHResult(((ID3D11DeviceContext*)_immediateContext)->Map(
                            (ID3D11Resource*)buffer.Buffer,
                            0,
                            D3D11Formats.VdToD3D11MapMode((buffer.Usage & BufferUsage.Dynamic) == BufferUsage.Dynamic, mode),
                            0,
                            &msr));

                        info.MappedResource = new MappedResource(resource, mode, (IntPtr)msr.PData, buffer.SizeInBytes);
                        info.RefCount = 1;
                        info.Mode = mode;
                        _mappedResources.Add(key, info);
                    }
                }
                else
                {
                    D3D11Texture texture = Util.AssertSubtype<MappableResource, D3D11Texture>(resource);
                    lock (_immediateContextLock)
                    {
                        MappedSubresource msr;
                        SilkMarshal.ThrowHResult(((ID3D11DeviceContext*)_immediateContext)->Map(
                            texture.DeviceTexture,
                            subresource,
                            D3D11Formats.VdToD3D11MapMode(false, mode),
                            0,
                            &msr));

                        info.MappedResource = new MappedResource(
                            resource,
                            mode,
                            (IntPtr)msr.PData,
                            texture.Height * msr.RowPitch,
                            subresource,
                            msr.RowPitch,
                            msr.DepthPitch);
                        info.RefCount = 1;
                        info.Mode = mode;
                        _mappedResources.Add(key, info);
                    }
                }
            }

            return info.MappedResource;
        }
    }

    protected override void UnmapCore(MappableResource resource, uint subresource)
    {
        MappedResourceCacheKey key = new MappedResourceCacheKey(resource, subresource);
        bool commitUnmap;

        lock (_mappedResourceLock)
        {
            if (!_mappedResources.TryGetValue(key, out MappedResourceInfo info))
            {
                throw NeoVeldridMappedResourceException.NotMapped(resource, subresource);
            }

            info.RefCount -= 1;
            commitUnmap = info.RefCount == 0;
            if (commitUnmap)
            {
                lock (_immediateContextLock)
                {
                    if (resource is D3D11Buffer buffer)
                    {
                        ((ID3D11DeviceContext*)_immediateContext)->Unmap((ID3D11Resource*)buffer.Buffer, 0);
                    }
                    else
                    {
                        D3D11Texture texture = Util.AssertSubtype<MappableResource, D3D11Texture>(resource);
                        ((ID3D11DeviceContext*)_immediateContext)->Unmap(texture.DeviceTexture, subresource);
                    }

                    bool result = _mappedResources.Remove(key);
                    Debug.Assert(result);
                }
            }
            else
            {
                _mappedResources[key] = info;
            }
        }
    }

    private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
    {
        D3D11Buffer d3dBuffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(buffer);
        if (sizeInBytes == 0)
        {
            return;
        }

        bool isDynamic = (buffer.Usage & BufferUsage.Dynamic) == BufferUsage.Dynamic;
        bool isStaging = (buffer.Usage & BufferUsage.Staging) == BufferUsage.Staging;
        bool isUniformBuffer = (buffer.Usage & BufferUsage.UniformBuffer) == BufferUsage.UniformBuffer;
        bool updateFullBuffer = bufferOffsetInBytes == 0 && sizeInBytes == buffer.SizeInBytes;
        bool useUpdateSubresource = (!isDynamic && !isStaging) && (!isUniformBuffer || updateFullBuffer);
        bool useMap = (isDynamic && updateFullBuffer) || isStaging;

        if (useUpdateSubresource)
        {
            Box subregion = new Box
            {
                Left = bufferOffsetInBytes,
                Top = 0,
                Front = 0,
                Right = sizeInBytes + bufferOffsetInBytes,
                Bottom = 1,
                Back = 1,
            };

            lock (_immediateContextLock)
            {
                if (isUniformBuffer)
                {
                    ((ID3D11DeviceContext*)_immediateContext)->UpdateSubresource(
                        (ID3D11Resource*)d3dBuffer.Buffer, 0, (Box*)null, (void*)source, 0, 0);
                }
                else
                {
                    ((ID3D11DeviceContext*)_immediateContext)->UpdateSubresource(
                        (ID3D11Resource*)d3dBuffer.Buffer, 0, &subregion, (void*)source, 0, 0);
                }
            }
        }
        else if (useMap)
        {
            MappedResource mr = MapCore(buffer, MapMode.Write, 0);
            if (sizeInBytes < 1024)
            {
                Unsafe.CopyBlock((byte*)mr.Data + bufferOffsetInBytes, source.ToPointer(), sizeInBytes);
            }
            else
            {
                Buffer.MemoryCopy(
                    source.ToPointer(),
                    (byte*)mr.Data + bufferOffsetInBytes,
                    buffer.SizeInBytes,
                    sizeInBytes);
            }
            UnmapCore(buffer, 0);
        }
        else
        {
            D3D11Buffer staging = GetFreeStagingBuffer(sizeInBytes);
            UpdateBuffer(staging, 0, source, sizeInBytes);
            Box sourceRegion = new Box
            {
                Left = 0,
                Top = 0,
                Front = 0,
                Right = sizeInBytes,
                Bottom = 1,
                Back = 1,
            };
            lock (_immediateContextLock)
            {
                ((ID3D11DeviceContext*)_immediateContext)->CopySubresourceRegion(
                    (ID3D11Resource*)d3dBuffer.Buffer, 0, bufferOffsetInBytes, 0, 0,
                    (ID3D11Resource*)staging.Buffer, 0,
                    &sourceRegion);
            }

            lock (_stagingResourcesLock)
            {
                _availableStagingBuffers.Add(staging);
            }
        }
    }

    private D3D11Buffer GetFreeStagingBuffer(uint sizeInBytes)
    {
        lock (_stagingResourcesLock)
        {
            foreach (D3D11Buffer buffer in _availableStagingBuffers)
            {
                if (buffer.SizeInBytes >= sizeInBytes)
                {
                    _availableStagingBuffers.Remove(buffer);
                    return buffer;
                }
            }
        }

        DeviceBuffer staging = ResourceFactory.CreateBuffer(
            new BufferDescription(sizeInBytes, BufferUsage.Staging));

        return Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(staging);
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
        D3D11Texture d3dTex = Util.AssertSubtype<Texture, D3D11Texture>(texture);
        bool useMap = (texture.Usage & TextureUsage.Staging) == TextureUsage.Staging;
        if (useMap)
        {
            uint subresource = texture.CalculateSubresource(mipLevel, arrayLayer);
            MappedResourceCacheKey key = new MappedResourceCacheKey(texture, subresource);
            MappedResource map = MapCore(texture, MapMode.Write, subresource);

            uint denseRowSize = FormatHelpers.GetRowPitch(width, texture.Format);
            uint denseSliceSize = FormatHelpers.GetDepthPitch(denseRowSize, height, texture.Format);

            Util.CopyTextureRegion(
                source.ToPointer(),
                0, 0, 0,
                denseRowSize, denseSliceSize,
                map.Data.ToPointer(),
                x, y, z,
                map.RowPitch, map.DepthPitch,
                width, height, depth,
                texture.Format);

            UnmapCore(texture, subresource);
        }
        else
        {
            int subresource = D3D11Util.ComputeSubresource(mipLevel, texture.MipLevels, arrayLayer);
            Box resourceRegion = D3D11Util.GetTextureRegion(
                d3dTex,
                x,
                y,
                z,
                width,
                height,
                depth,
                mipLevel);
            Box* nativeRegion = D3D11Util.IsFullTextureSubresource(
                d3dTex,
                mipLevel,
                in resourceRegion)
                ? null
                : &resourceRegion;

            uint srcRowPitch = FormatHelpers.GetRowPitch(width, texture.Format);
            uint srcDepthPitch = FormatHelpers.GetDepthPitch(srcRowPitch, height, texture.Format);
            lock (_immediateContextLock)
            {
                ((ID3D11DeviceContext*)_immediateContext)->UpdateSubresource(
                    d3dTex.DeviceTexture,
                    (uint)subresource,
                    nativeRegion,
                    (void*)source,
                    srcRowPitch,
                    srcDepthPitch);
            }
        }
    }

    public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
    {
        if (RequiresValidationBoundary)
        {
            FenceWaitBoundary boundary = new FenceWaitBoundary(
                this,
                fence,
                nanosecondTimeout);
            ExecuteValidationBoundary(
                "fence wait",
                boundary,
                static state => state.Result = state.Device.WaitForFenceCore(
                    state.Fence,
                    state.NanosecondTimeout));
            return boundary.Result;
        }

        return WaitForFenceCore(fence, nanosecondTimeout);
    }

    private bool WaitForFenceCore(Fence fence, ulong nanosecondTimeout)
    {
        D3D11Fence d3d11Fence = GetOwnedFence(fence);
        long startTimestamp = Stopwatch.GetTimestamp();
        SpinWait spinner = default;
        while (true)
        {
            if (IsFenceSignaledCore(d3d11Fence))
            {
                return true;
            }
            if (FenceWaitTimedOut(startTimestamp, nanosecondTimeout))
            {
                return false;
            }

            spinner.SpinOnce();
        }
    }

    public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
    {
        if (RequiresValidationBoundary)
        {
            FenceSetWaitBoundary boundary = new FenceSetWaitBoundary(
                this,
                fences,
                waitAll,
                nanosecondTimeout);
            ExecuteValidationBoundary(
                "fence-set wait",
                boundary,
                static state => state.Result = state.Device.WaitForFencesCore(
                    state.Fences,
                    state.WaitAll,
                    state.NanosecondTimeout));
            return boundary.Result;
        }

        return WaitForFencesCore(fences, waitAll, nanosecondTimeout);
    }

    private bool WaitForFencesCore(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
    {
        ArgumentNullException.ThrowIfNull(fences);
        if (fences.Length == 0)
        {
            throw new ArgumentException("At least one fence is required.", nameof(fences));
        }

        D3D11Fence[] d3d11Fences = new D3D11Fence[fences.Length];
        for (int i = 0; i < fences.Length; i++)
        {
            d3d11Fences[i] = GetOwnedFence(fences[i]);
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        SpinWait spinner = default;
        while (true)
        {
            bool anySignaled = false;
            bool allSignaled = true;
            lock (_immediateContextLock)
            {
                ID3D11DeviceContext* context = _immediateContext.Handle;
                for (int i = 0; i < d3d11Fences.Length; i++)
                {
                    bool signaled = d3d11Fences[i].Poll(context);
                    anySignaled |= signaled;
                    allSignaled &= signaled;
                }
            }

            if (waitAll ? allSignaled : anySignaled)
            {
                return true;
            }
            if (FenceWaitTimedOut(startTimestamp, nanosecondTimeout))
            {
                return false;
            }

            spinner.SpinOnce();
        }
    }

    internal bool IsFenceSignaled(D3D11Fence fence)
    {
        fence.ValidateOwner(this);
        if (RequiresValidationBoundary)
        {
            FenceStatusBoundary boundary = new FenceStatusBoundary(this, fence);
            ExecuteValidationBoundary(
                "fence status",
                boundary,
                static state => state.Result = state.Device.IsFenceSignaledCore(state.Fence));
            return boundary.Result;
        }

        return IsFenceSignaledCore(fence);
    }

    private bool IsFenceSignaledCore(D3D11Fence fence)
    {
        lock (_immediateContextLock)
        {
            return fence.Poll(_immediateContext.Handle);
        }
    }

    private static bool FenceWaitTimedOut(long startTimestamp, ulong nanosecondTimeout)
    {
        if (nanosecondTimeout == ulong.MaxValue)
        {
            return false;
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        double elapsedNanoseconds =
            elapsedTicks * (1_000_000_000d / Stopwatch.Frequency);
        return elapsedNanoseconds >= nanosecondTimeout;
    }

    private sealed class FenceWaitBoundary
    {
        public FenceWaitBoundary(
            D3D11GraphicsDevice device,
            Fence fence,
            ulong nanosecondTimeout)
        {
            Device = device;
            Fence = fence;
            NanosecondTimeout = nanosecondTimeout;
        }

        public D3D11GraphicsDevice Device { get; }
        public Fence Fence { get; }
        public ulong NanosecondTimeout { get; }
        public bool Result { get; set; }
    }

    private sealed class FenceStatusBoundary
    {
        public FenceStatusBoundary(D3D11GraphicsDevice device, D3D11Fence fence)
        {
            Device = device;
            Fence = fence;
        }

        public D3D11GraphicsDevice Device { get; }
        public D3D11Fence Fence { get; }
        public bool Result { get; set; }
    }

    private sealed class FenceResetBoundary
    {
        public FenceResetBoundary(D3D11GraphicsDevice device, D3D11Fence fence)
        {
            Device = device;
            Fence = fence;
        }

        public D3D11GraphicsDevice Device { get; }
        public D3D11Fence Fence { get; }
    }

    private sealed class FenceSetWaitBoundary
    {
        public FenceSetWaitBoundary(
            D3D11GraphicsDevice device,
            Fence[] fences,
            bool waitAll,
            ulong nanosecondTimeout)
        {
            Device = device;
            Fences = fences;
            WaitAll = waitAll;
            NanosecondTimeout = nanosecondTimeout;
        }

        public D3D11GraphicsDevice Device { get; }
        public Fence[] Fences { get; }
        public bool WaitAll { get; }
        public ulong NanosecondTimeout { get; }
        public bool Result { get; set; }
    }

    public override void ResetFence(Fence fence)
    {
        ResetFenceState(GetOwnedFence(fence));
    }

    internal void ResetFenceState(D3D11Fence fence)
    {
        fence.ValidateOwner(this);
        if (RequiresValidationBoundary)
        {
            ExecuteValidationBoundary(
                "fence reset",
                new FenceResetBoundary(this, fence),
                static state => state.Device.ResetFenceStateCore(state.Fence));
            return;
        }

        ResetFenceStateCore(fence);
    }

    private D3D11Fence GetOwnedFence(Fence fence)
    {
        D3D11Fence d3d11Fence = Util.AssertSubtype<Fence, D3D11Fence>(fence);
        d3d11Fence.ValidateOwner(this);
        return d3d11Fence;
    }

    private void ResetFenceStateCore(D3D11Fence fence)
    {
        lock (_immediateContextLock)
        {
            fence.ResetForReuse(_immediateContext.Handle);
        }
    }

    internal override uint GetUniformBufferMinOffsetAlignmentCore() => 256u;

    internal override uint GetStructuredBufferMinOffsetAlignmentCore() => 16;

    protected override void PlatformDispose()
    {
        List<Exception> failures = null;

        foreach (DeviceBuffer buffer in _availableStagingBuffers)
        {
            AttemptPlatformCleanup(buffer.Dispose, ref failures);
        }
        _availableStagingBuffers.Clear();

        AttemptPlatformCleanup(() => _d3d11ResourceFactory?.Dispose(), ref failures);
        AttemptPlatformCleanup(() => _mainSwapchain?.Dispose(), ref failures);
        if (_immediateContext.Handle != null)
        {
            AttemptPlatformCleanup(_immediateContext.Dispose, ref failures);
        }
        _immediateContext = default;
        if (_dxgiAdapter.Handle != null)
        {
            AttemptPlatformCleanup(_dxgiAdapter.Dispose, ref failures);
        }
        _dxgiAdapter = default;

        // Copy operational and cleanup messages while the device and queue are
        // unquestionably alive. The device-owned validation history outlives
        // every native interface below.
        AttemptPlatformCleanup(
            () => _validationMessages?.DrainTo(Validation),
            ref failures);

        // The debug and info-queue interfaces deliberately keep the device alive
        // after NeoVeldrid releases its reference. This makes the live-object
        // report deterministic and lets it identify any remaining child object.
        if (_device.Handle != null)
        {
            AttemptPlatformCleanup(_device.Dispose, ref failures);
        }
        _device = default;
        AttemptPlatformCleanup(
            () => _validationMessages?.ReportLiveObjectsAndDrainTo(Validation),
            ref failures);
        AttemptPlatformCleanup(() => _validationMessages?.Dispose(), ref failures);

        AttemptPlatformCleanup(ReportDxgiLiveObjectsBestEffort, ref failures);
        AttemptPlatformCleanup(() => _d3d11Api?.Dispose(), ref failures);

        if (failures?.Count == 1)
        {
            throw failures[0];
        }
        if (failures?.Count > 1)
        {
            throw new AggregateException(
                "Direct3D 11 teardown encountered multiple failures.",
                failures);
        }
    }

    private void ReportDxgiLiveObjectsBestEffort()
    {
        if (!_debugDeviceCreated || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
#pragma warning disable CS0618
            using var dxgi = DXGI.GetApi();
#pragma warning restore CS0618
            IDXGIDebug1* dxgiDebug = null;
            try
            {
                Guid dxgiDebugGuid = IDXGIDebug1.Guid;
                int activationResult =
                    dxgi.GetDebugInterface1(0, &dxgiDebugGuid, (void**)&dxgiDebug);
                if (activationResult < 0)
                {
                    Validation.Report(
                        GraphicsDeviceValidationSeverity.Warning,
                        "DXGI",
                        "LiveObjectTracking",
                        "DebugInterfaceActivationFailed",
                        $"DXGI.GetDebugInterface1 failed with HRESULT 0x{activationResult:X8}.");
                    return;
                }
                if (dxgiDebug == null)
                {
                    Validation.Report(
                        GraphicsDeviceValidationSeverity.Warning,
                        "DXGI",
                        "LiveObjectTracking",
                        "DebugInterfaceActivationReturnedNull",
                        "DXGI.GetDebugInterface1 succeeded without returning IDXGIDebug1.");
                    return;
                }

                Guid debugAll = DxgiDebugAll;
                int reportResult = dxgiDebug->ReportLiveObjects(
                    debugAll,
                    DebugRloFlags.Summary | DebugRloFlags.IgnoreInternal);
                if (reportResult < 0)
                {
                    Validation.Report(
                        GraphicsDeviceValidationSeverity.Warning,
                        "DXGI",
                        "LiveObjectTracking",
                        "ReportLiveObjectsFailed",
                        $"IDXGIDebug1.ReportLiveObjects failed with HRESULT 0x{reportResult:X8}.");
                }
            }
            finally
            {
                if (dxgiDebug != null)
                {
                    dxgiDebug->Release();
                }
            }
        }
        catch (Exception exception)
        {
            // DXGIGetDebugInterface1 is not guaranteed on every supported Windows
            // version. Keep it as evidence without weakening the D3D11 queue gate.
            Validation.Report(
                GraphicsDeviceValidationSeverity.Warning,
                "DXGI",
                "LiveObjectTracking",
                "ReportUnavailable",
                exception.Message);
        }
    }

    private static void AttemptPlatformCleanup(Action cleanup, ref List<Exception> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures ??= new List<Exception>();
            failures.Add(exception);
        }
    }

    private protected override void WaitForIdleCore()
    {
        QueryDesc completionQueryDescription = new QueryDesc
        {
            Query = Silk.NET.Direct3D11.Query.Event,
            MiscFlags = 0,
        };
        ID3D11Query* completionQuery = null;
        try
        {
            SilkMarshal.ThrowHResult(
                _device.Handle->CreateQuery(
                    &completionQueryDescription,
                    &completionQuery));

            // Every use of the immediate context is serialized by this lock.
            // Keep ownership through the event-query wait so no later submit can
            // cross the idle boundary while it is being established.
            lock (_immediateContextLock)
            {
                ID3D11DeviceContext* context = _immediateContext.Handle;
                context->End((ID3D11Asynchronous*)completionQuery);
                context->Flush();

                SpinWait spinner = default;
                while (true)
                {
                    int result = context->GetData(
                        (ID3D11Asynchronous*)completionQuery,
                        null,
                        0,
                        (uint)AsyncGetdataFlag.Donotflush);
                    if (result == 0) // S_OK
                    {
                        break;
                    }

                    if (result < 0)
                    {
                        SilkMarshal.ThrowHResult(result);
                    }
                    if (result != 1) // S_FALSE is the only pending result.
                    {
                        throw new NeoVeldridException(
                            $"ID3D11DeviceContext.GetData returned unexpected status 0x{result:X8} while waiting for device idle.");
                    }

                    spinner.SpinOnce();
                }
            }
        }
        finally
        {
            // CreateQuery may populate its output even when it reports failure.
            if (completionQuery != null)
            {
                completionQuery->Release();
            }
        }
    }

    protected override void CollectValidationMessages()
    {
        _validationMessages?.DrainTo(Validation);
    }

    public override bool GetD3D11Info(out BackendInfoD3D11 info)
    {
        info = _d3d11Info;
        return true;
    }
}
