using static NeoVeldrid.OpenGL.OpenGLUtil;
using System;
using Silk.NET.Core.Loader;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.EXT;
using GLPixelFormat = Silk.NET.OpenGL.PixelFormat;
using GLFramebufferAttachment = Silk.NET.OpenGL.FramebufferAttachment;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace NeoVeldrid.OpenGL;

internal unsafe class OpenGLGraphicsDevice : GraphicsDevice
{
    private ResourceFactory _resourceFactory;
    private string _deviceName;
    private string _vendorName;
    private string _version;
    private string _shadingLanguageVersion;
    private GraphicsApiVersion _apiVersion;
    private GraphicsBackend _backendType;
    private GraphicsDeviceFeatures _features;
    private uint _vao;
    private readonly ConcurrentQueue<OpenGLDeferredResource> _resourcesToDispose
        = new ConcurrentQueue<OpenGLDeferredResource>();
    private IntPtr _glContext;
    private bool _glContextUnavailable;
    private bool _glContextDestructionConfirmed;
    private Func<string, IntPtr> _getProcAddress;
    private Action<IntPtr> _makeCurrent;
    private Func<IntPtr> _getCurrentContext;
    private Action _clearCurrentContext;
    private Action<IntPtr> _deleteContext;
    private Action _swapBuffers;
    private Action<bool> _setSyncToVBlank;
    private OpenGLSwapchainFramebuffer _swapchainFramebuffer;
    private OpenGLTextureSamplerManager _textureSamplerManager;
    private OpenGLCommandExecutor _commandExecutor;
    private OpenGLDebugOutput _debugOutput;
    public GL GL { get; private set; }
    private OpenGLExtensions _extensions;
    private bool _isDepthRangeZeroToOne;
    private bool _isDebugContext;
    private bool _debugContextStatusKnown;
    private int _contextFlags;

    // EXT_debug_marker (GLES extension for GPU profiling tools like Xcode GPU debugger).
    internal ExtDebugMarker _extDebugMarker;

    // Silk.NET maps GL.ClearDepth(float) to glClearDepthf (GL 4.1+ / GLES) and
    // GL.ClearDepth(double) to glClearDepth (desktop GL). Same for DepthRange.
    // Desktop GL below 4.1 doesn't have glClearDepthf, so we must use the double variant there.
    internal void ClearDepthCompat(float depth)
    {
        if (_backendType == GraphicsBackend.OpenGLES)
            GL.ClearDepth(depth);         // float overload -> glClearDepthf
        else
            GL.ClearDepth((double)depth); // double overload -> glClearDepth
    }

    internal void DepthRangeCompat(float near, float far)
    {
        if (_backendType == GraphicsBackend.OpenGLES)
            GL.DepthRange(near, far);         // float overload -> glDepthRangef
        else
            GL.DepthRange((double)near, (double)far); // double overload -> glDepthRange
    }
    private BackendInfoOpenGL _openglInfo;

    private TextureSampleCount _maxColorTextureSamples;
    private uint _maxTextureSize;
    private uint _maxTexDepth;
    private uint _maxTexArrayLayers;
    private uint _minUboOffsetAlignment;
    private uint _minSsboOffsetAlignment;

    private readonly StagingMemoryPool _stagingMemoryPool = new StagingMemoryPool();
    private BlockingCollection<ExecutionThreadWorkItem> _workItems;
    private ExecutionThread _executionThread;
    private readonly object _commandListDisposalLock = new object();
    private readonly Dictionary<OpenGLCommandList, int> _submittedCommandListCounts
        = new Dictionary<OpenGLCommandList, int>();
    private readonly HashSet<OpenGLCommandList> _commandListsToDispose = new HashSet<OpenGLCommandList>();

    private readonly object _mappedResourceLock = new object();
    private readonly Dictionary<MappedResourceCacheKey, MappedResourceInfoWithStaging> _mappedResources
        = new Dictionary<MappedResourceCacheKey, MappedResourceInfoWithStaging>();

    private readonly object _resetEventsLock = new object();
    private readonly List<ManualResetEvent[]> _resetEvents = new List<ManualResetEvent[]>();
    private Swapchain _mainSwapchain;

    private bool _syncToVBlank;

    public override string DeviceName => _deviceName;

    public override string VendorName => _vendorName;

    public override GraphicsApiVersion ApiVersion => _apiVersion;

    public override GraphicsBackend BackendType => _backendType;

    public override bool IsUvOriginTopLeft => false;

    public override bool IsDepthRangeZeroToOne => _isDepthRangeZeroToOne;

    public override bool IsClipSpaceYInverted => false;

    public override ResourceFactory ResourceFactory => _resourceFactory;

    public OpenGLExtensions Extensions => _extensions;

    public override Swapchain MainSwapchain => _mainSwapchain;

    public override bool SyncToVerticalBlank
    {
        get => _syncToVBlank;
        set
        {
            if (_syncToVBlank != value)
            {
                _syncToVBlank = value;
                _executionThread.SetSyncToVerticalBlank(value);
            }
        }
    }

    public string Version => _version;

    public string ShadingLanguageVersion => _shadingLanguageVersion;

    internal bool IsDebugContext => _isDebugContext;

    internal bool IsDebugContextStatusKnown => _debugContextStatusKnown;

    internal int ContextFlags => _contextFlags;

    internal string DebugOutputTransport => _debugOutput?.Transport ?? string.Empty;

    internal bool IsDebugOutputSynchronous => _debugOutput?.SynchronousDelivery ?? false;

    internal bool DebugOutputProbePassed => _debugOutput?.ProbePassed ?? false;

    public OpenGLTextureSamplerManager TextureSamplerManager => _textureSamplerManager;

    public override GraphicsDeviceFeatures Features => _features;

    public StagingMemoryPool StagingMemoryPool => _stagingMemoryPool;

    public OpenGLGraphicsDevice(
        GraphicsDeviceOptions options,
        OpenGLPlatformInfo platformInfo,
        uint width,
        uint height)
    {
        ArgumentNullException.ThrowIfNull(platformInfo);

        try
        {
            Init(options, platformInfo, width, height, true);
        }
        catch (Exception initializationError)
        {
            FailDeviceCreation(initializationError);
        }
    }

    private void Init(
        GraphicsDeviceOptions options,
        OpenGLPlatformInfo platformInfo,
        uint width,
        uint height,
        bool loadFunctions)
    {
        // Device bootstrap has no active debug callback to report native failures.
        // Intentionally shadow the debug-only hot-path helper in this scope so every
        // initialization call is checked in Release as well as Debug builds.
        void CheckLastError() => CheckInitializationError("device bootstrap");

        _syncToVBlank = options.SyncToVerticalBlank;
        _glContext = platformInfo.OpenGLContextHandle;
        _getProcAddress = platformInfo.GetProcAddress;
        _makeCurrent = platformInfo.MakeCurrent;
        _getCurrentContext = platformInfo.GetCurrentContext;
        _clearCurrentContext = platformInfo.ClearCurrentContext;
        _deleteContext = platformInfo.DeleteContext;
        _swapBuffers = platformInfo.SwapBuffers;
        _setSyncToVBlank = platformInfo.SetSyncToVerticalBlank;

        MakeOwnedContextCurrent("device initialization");
        if (loadFunctions)
        {
            GL = GL.GetApi(platformInfo.GetProcAddress);
            OpenGLUtil.GL = GL;
        }
        Debug.Assert(GL != null, "GL instance must be set before Init(). If loadFunctions=false, the caller must set GL beforehand.");
        _version = GL.GetStringS(StringName.Version);
        CheckInitializationError("GL_VERSION query");
        _shadingLanguageVersion = GL.GetStringS(StringName.ShadingLanguageVersion);
        CheckInitializationError("GL_SHADING_LANGUAGE_VERSION query");
        _vendorName = GL.GetStringS(StringName.Vendor);
        CheckInitializationError("GL_VENDOR query");
        _deviceName = GL.GetStringS(StringName.Renderer);
        CheckInitializationError("GL_RENDERER query");
        _backendType = _version.StartsWith("OpenGL ES") ? GraphicsBackend.OpenGLES : GraphicsBackend.OpenGL;
        InitializeValidation(_backendType, options.Debug);

        // ClearDepthf/DepthRangef are available via GL.ClearDepth(float)/GL.DepthRange(float, float)
        // (core in GL 4.1+ from ARB_ES2_compatibility, and always available in GLES).

        int majorVersion, minorVersion;
        GL.GetInteger(GetPName.MajorVersion, out majorVersion);
        CheckLastError();
        GL.GetInteger(GetPName.MinorVersion, out minorVersion);
        CheckLastError();

        GraphicsApiVersion.TryParseGLVersion(_version, out _apiVersion);
        if (_apiVersion.Major != majorVersion ||
            _apiVersion.Minor != minorVersion)
        {
            // This mismatch should never be hit in valid OpenGL implementations.
            _apiVersion = new GraphicsApiVersion(majorVersion, minorVersion, 0, 0);
        }

        int extensionCount;
        GL.GetInteger(GetPName.NumExtensions, out extensionCount);
        CheckLastError();

        HashSet<string> extensions = new HashSet<string>();
        for (uint i = 0; i < extensionCount; i++)
        {
            byte* extensionNamePtr = GL.GetString(StringName.Extensions, i);
            CheckLastError();
            if (extensionNamePtr != null)
            {
                string extensionName = Util.GetString(extensionNamePtr);
                extensions.Add(extensionName);
            }
        }

        _extensions = new OpenGLExtensions(extensions, _backendType, majorVersion, minorVersion);
        OpenGLUtil.HasGlObjectLabel = _extensions.KHR_Debug;

        if (_extensions.GLVersion(3, 0) || _extensions.GLESVersion(3, 2))
        {
            GL.GetInteger((GetPName)0x821E, out _contextFlags); // GL_CONTEXT_FLAGS
            CheckLastError();
            _debugContextStatusKnown = true;
            _isDebugContext = (_contextFlags & 0x00000002) != 0; // GL_CONTEXT_FLAG_DEBUG_BIT
        }
        else if (platformInfo.IsDebugContext.HasValue)
        {
            _debugContextStatusKnown = true;
            _isDebugContext = platformInfo.IsDebugContext.Value;
            _contextFlags = _isDebugContext ? 0x00000002 : 0;
        }

        if (_extensions.EXT_DebugMarker)
        {
            GL.TryGetExtension(out _extDebugMarker);
        }

        bool drawIndirect = _extensions.DrawIndirect || _extensions.MultiDrawIndirect;
        _features = new GraphicsDeviceFeatures(
            computeShader: _extensions.ComputeShaders,
            geometryShader: _extensions.GeometryShader,
            tessellationShaders: _extensions.TessellationShader,
            multipleViewports: _extensions.ARB_ViewportArray,
            samplerLodBias: _backendType == GraphicsBackend.OpenGL,
            drawBaseVertex: _extensions.DrawElementsBaseVertex,
            drawBaseInstance: _extensions.GLVersion(4, 2),
            drawIndirect: drawIndirect,
            drawIndirectBaseInstance: drawIndirect,
            fillModeWireframe: _backendType == GraphicsBackend.OpenGL,
            samplerAnisotropy: _extensions.AnisotropicFilter,
            depthClipDisable: _backendType == GraphicsBackend.OpenGL,
            texture1D: _backendType == GraphicsBackend.OpenGL,
            independentBlend: _extensions.IndependentBlend,
            structuredBuffer: _extensions.StorageBuffers,
            subsetTextureView: _extensions.ARB_TextureView,
            commandListDebugMarkers: _extensions.KHR_Debug || _extensions.EXT_DebugMarker,
            bufferRangeBinding: _extensions.ARB_uniform_buffer_object,
            shaderFloat64: _extensions.ARB_GpuShaderFp64);

        int uboAlignment;
        GL.GetInteger(GetPName.UniformBufferOffsetAlignment, out uboAlignment);
        CheckLastError();
        _minUboOffsetAlignment = (uint)uboAlignment;

        if (_features.StructuredBuffer)
        {
            int ssboAlignment;
            GL.GetInteger(GetPName.ShaderStorageBufferOffsetAlignment, out ssboAlignment);
            CheckLastError();
            _minSsboOffsetAlignment = (uint)ssboAlignment;
        }

        _resourceFactory = new OpenGLResourceFactory(this);

        _vao = GL.GenVertexArray();
        CheckLastError();

        GL.BindVertexArray(_vao);
        CheckLastError();

        if (options.Debug)
        {
            ActivateDebugOutput();
            CheckLastError();
        }

        bool backbufferIsSrgb = ManualSrgbBackbufferQuery();

        PixelFormat swapchainFormat;
        if (options.SwapchainSrgbFormat && (backbufferIsSrgb || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)))
        {
            swapchainFormat = PixelFormat.B8_G8_R8_A8_UNorm_SRgb;
        }
        else
        {
            swapchainFormat = PixelFormat.B8_G8_R8_A8_UNorm;
        }

        _swapchainFramebuffer = new OpenGLSwapchainFramebuffer(
            width,
            height,
            swapchainFormat,
            options.SwapchainDepthFormat,
            swapchainFormat != PixelFormat.B8_G8_R8_A8_UNorm_SRgb);

        // Set miscellaneous initial states.
        if (_backendType == GraphicsBackend.OpenGL)
        {
            GL.Enable(EnableCap.TextureCubeMapSeamless);
            CheckLastError();
        }

        _textureSamplerManager = new OpenGLTextureSamplerManager(this, _extensions);
        _commandExecutor = new OpenGLCommandExecutor(this, platformInfo);

        int maxColorTextureSamples;
        if (_backendType == GraphicsBackend.OpenGL)
        {
            GL.GetInteger(GetPName.MaxColorTextureSamples, out maxColorTextureSamples);
            CheckLastError();
        }
        else
        {
            GL.GetInteger((GetPName)GLEnum.MaxSamples, out maxColorTextureSamples);
            CheckLastError();
        }
        if (maxColorTextureSamples >= 32)
        {
            _maxColorTextureSamples = TextureSampleCount.Count32;
        }
        else if (maxColorTextureSamples >= 16)
        {
            _maxColorTextureSamples = TextureSampleCount.Count16;
        }
        else if (maxColorTextureSamples >= 8)
        {
            _maxColorTextureSamples = TextureSampleCount.Count8;
        }
        else if (maxColorTextureSamples >= 4)
        {
            _maxColorTextureSamples = TextureSampleCount.Count4;
        }
        else if (maxColorTextureSamples >= 2)
        {
            _maxColorTextureSamples = TextureSampleCount.Count2;
        }
        else
        {
            _maxColorTextureSamples = TextureSampleCount.Count1;
        }

        int maxTexSize;

        GL.GetInteger(GetPName.MaxTextureSize, out maxTexSize);
        CheckLastError();

        int maxTexDepth;
        GL.GetInteger(GetPName.Max3DTextureSize, out maxTexDepth);
        CheckLastError();

        int maxTexArrayLayers;
        GL.GetInteger(GetPName.MaxArrayTextureLayers, out maxTexArrayLayers);
        CheckLastError();

        if (options.PreferDepthRangeZeroToOne && _extensions.ARB_ClipControl)
        {
            GL.ClipControl(ClipControlOrigin.LowerLeft, ClipControlDepth.ZeroToOne);
            CheckLastError();
            _isDepthRangeZeroToOne = true;
        }

        _maxTextureSize = (uint)maxTexSize;
        _maxTexDepth = (uint)maxTexDepth;
        _maxTexArrayLayers = (uint)maxTexArrayLayers;

        _mainSwapchain = new OpenGLSwapchain(
            this,
            _swapchainFramebuffer,
            platformInfo.ResizeSwapchain);

        _workItems = new BlockingCollection<ExecutionThreadWorkItem>(new ConcurrentQueue<ExecutionThreadWorkItem>());
        ClearOwnedContext("execution-thread handoff");
        _executionThread = new ExecutionThread(
            this,
            _workItems,
            _makeCurrent,
            _getCurrentContext,
            _glContext);
        _openglInfo = new BackendInfoOpenGL(this);

        CompleteDeviceCreation();
        _executionThread.Run(
            () => CheckInitializationError("common device-resource bootstrap"));
    }

    private void MakeOwnedContextCurrent(string operation)
    {
        _makeCurrent(_glContext);
        IntPtr currentContext = _getCurrentContext();
        if (currentContext != _glContext)
        {
            throw new NeoVeldridException(
                $"The platform did not make the owned OpenGL context current during {operation}. "
                + $"Expected 0x{_glContext.ToInt64():X}, but observed 0x{currentContext.ToInt64():X}.");
        }
    }

    private void ClearOwnedContext(string operation)
    {
        _clearCurrentContext();
        IntPtr currentContext = _getCurrentContext();
        if (currentContext != IntPtr.Zero)
        {
            throw new NeoVeldridException(
                $"The platform did not clear the OpenGL context during {operation}. "
                + $"Context 0x{currentContext.ToInt64():X} remained current.");
        }
    }

    private void CheckInitializationError(string operation)
    {
        uint error = (uint)GL.GetError();
        if (error != 0)
        {
            throw new NeoVeldridException(
                $"OpenGL initialization failed during {operation}: glGetError returned {(ErrorCode)error}.");
        }
    }

    private void ActivateDebugOutput()
    {
        if (!_debugContextStatusKnown)
        {
            Validation.SetInactive(
                "the platform could not attest whether this OpenGL context was created with the debug flag");
            return;
        }

        if (!_isDebugContext)
        {
            Validation.SetInactive("the OpenGL context was created without the debug flag");
            return;
        }

        _debugOutput = OpenGLDebugOutput.TryCreate(
            GL,
            _getProcAddress,
            _extensions,
            _backendType,
            Validation);
        if (_debugOutput == null)
        {
            string reason = _extensions.ARB_DebugOutput
                ? "GL_ARB_debug_output is available, but its ARB-only transport is not implemented"
                : "KHR/core OpenGL debug output is unavailable or its required entry points could not be loaded";
            Validation.SetInactive(reason);
            return;
        }

        if (_debugOutput.TryActivate(_isDebugContext, out string inactiveReason))
        {
            Validation.SetActive(
                GraphicsDeviceValidationFeatures.ApiDebugOutput
                | GraphicsDeviceValidationFeatures.SynchronousMessageDelivery,
                _debugOutput.Transport);
        }
        else
        {
            Validation.SetInactive(inactiveReason);
        }
    }

    private bool ManualSrgbBackbufferQuery()
    {
        // This probe runs before ownership moves to the execution thread and before
        // device creation is committed, so its errors must be visible in Release builds.
        void CheckLastError() => CheckInitializationError("sRGB backbuffer probe");

        if (_backendType == GraphicsBackend.OpenGLES && !_extensions.EXT_sRGBWriteControl)
        {
            return false;
        }

        uint copySrc = GL.GenTexture();
        CheckLastError();

        float* data = stackalloc float[4];
        data[0] = 0.5f;
        data[1] = 0.5f;
        data[2] = 0.5f;
        data[3] = 1f;

        GL.ActiveTexture(TextureUnit.Texture0);
        CheckLastError();
        GL.BindTexture(TextureTarget.Texture2D, copySrc);
        CheckLastError();
        GL.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, 1, 1, 0, GLPixelFormat.Rgba, PixelType.Float, data);
        CheckLastError();
        uint copySrcFb = GL.GenFramebuffer();
        CheckLastError();

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, copySrcFb);
        CheckLastError();
        GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, GLFramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, copySrc, 0);
        CheckLastError();

        GL.Enable(EnableCap.FramebufferSrgb);
        CheckLastError();
        GL.BlitFramebuffer(
            0, 0, 1, 1,
            0, 0, 1, 1,
            ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Nearest);
        CheckLastError();

        GL.Disable(EnableCap.FramebufferSrgb);
        CheckLastError();

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        CheckLastError();
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, copySrcFb);
        CheckLastError();
        GL.BlitFramebuffer(
            0, 0, 1, 1,
            0, 0, 1, 1,
            ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Nearest);
        CheckLastError();
        if (_backendType == GraphicsBackend.OpenGLES)
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, copySrc);
            CheckLastError();
            GL.ReadPixels(
                0, 0, 1, 1,
                GLPixelFormat.Rgba,
                PixelType.Float,
                data);
            CheckLastError();
        }
        else
        {
            GL.GetTexImage(TextureTarget.Texture2D, 0, GLPixelFormat.Rgba, PixelType.Float, data);
            CheckLastError();
        }

        GL.DeleteFramebuffer(copySrcFb);
        CheckLastError();
        GL.DeleteTexture(copySrc);
        CheckLastError();

        return data[0] > 0.6f;
    }

    private static int GetDepthBits(PixelFormat value)
    {
        switch (value)
        {
            case PixelFormat.R16_UNorm:
                return 16;
            case PixelFormat.R32_Float:
                return 32;
            default:
                throw new NeoVeldridException($"Unsupported depth format: {value}");
        }
    }

    private protected override void SubmitCommandsCore(
        CommandList cl,
        Fence fence)
    {
        OpenGLCommandList glCommandList = Util.AssertSubtype<CommandList, OpenGLCommandList>(cl);
        OpenGLFence glFence = fence == null
            ? null
            : Util.AssertSubtype<Fence, OpenGLFence>(fence);
        _executionThread.ExecuteCommands(
            glCommandList.CurrentCommands,
            glFence,
            waitForNativeCompletion: RequiresValidationBoundary);
    }

    private int IncrementCount(OpenGLCommandList glCommandList)
    {
        if (_submittedCommandListCounts.TryGetValue(glCommandList, out int count))
        {
            count += 1;
        }
        else
        {
            count = 1;
        }

        _submittedCommandListCounts[glCommandList] = count;
        return count;
    }

    private int DecrementCount(OpenGLCommandList glCommandList)
    {
        if (_submittedCommandListCounts.TryGetValue(glCommandList, out int count))
        {
            count -= 1;
        }
        else
        {
            count = -1;
        }

        if (count == 0)
        {
            _submittedCommandListCounts.Remove(glCommandList);
        }
        else
        {
            _submittedCommandListCounts[glCommandList] = count;
        }
        return count;
    }

    private int GetCount(OpenGLCommandList glCommandList)
    {
        return _submittedCommandListCounts.TryGetValue(glCommandList, out int count) ? count : 0;
    }

    private protected override void SwapBuffersCore(Swapchain swapchain)
    {
        WaitForIdle();

        _executionThread.SwapBuffers();
    }

    private protected override void WaitForIdleCore()
    {
        _executionThread.WaitForIdle();
    }

    private protected override void ExecuteWaitForIdle(Action waitForIdle)
    {
        ExecutionThread executionThread = _executionThread;
        executionThread?.ThrowIfExecutingGpuWork("wait for the device to become idle");
        if (executionThread == null
            || (executionThread.IsExecutionThread
                && executionThread.IsExecutingSynchronousWorkItem))
        {
            waitForIdle();
            return;
        }

        executionThread.Run(waitForIdle);
    }

    internal override void ExecuteValidationBoundary<TState>(
        string boundary,
        TState state,
        Action<TState> operation)
    {
        ExecutionThread executionThread = _executionThread;
        executionThread?.ThrowIfExecutingGpuWork(
            $"execute the '{boundary}' validation boundary");
        if (executionThread == null
            || (executionThread.IsExecutionThread
                && executionThread.IsExecutingSynchronousWorkItem))
        {
            base.ExecuteValidationBoundary(boundary, state, operation);
            return;
        }

        executionThread.Run(
            () => base.ExecuteValidationBoundary(boundary, state, operation));
    }

    private protected override IReadOnlyList<GraphicsDeviceValidationMessage> CheckValidationCore(
        string boundary)
    {
        ExecutionThread executionThread = _executionThread;
        executionThread?.ThrowIfExecutingGpuWork(
            $"check validation at the '{boundary}' boundary");
        if (executionThread == null
            || (executionThread.IsExecutionThread
                && executionThread.IsExecutingSynchronousWorkItem))
        {
            return base.CheckValidationCore(boundary);
        }

        IReadOnlyList<GraphicsDeviceValidationMessage> observed = null;
        executionThread.Run(() => observed = base.CheckValidationCore(boundary));
        return observed;
    }

    private protected override void ExecuteDeviceDisposal(Action disposeCore)
    {
        ExecutionThread executionThread = _executionThread;
        executionThread?.ThrowIfExecutingGpuWork("dispose the graphics device");
        if (executionThread == null || executionThread.IsExecutionThread)
        {
            disposeCore();
            return;
        }

        if (IsDisposed)
        {
            disposeCore();
            return;
        }

        if (!executionThread.TryRunDisposal(
            disposeCore,
            out ExceptionDispatchInfo proxyFailure,
            out bool ownsTerminatedThreadJoin))
        {
            // Admission may close after the optimistic IsDisposed check. In
            // that case the execution-thread owner no longer depends on this
            // caller, so joining the already-published shared transaction is safe.
            disposeCore();
            return;
        }

        // The admitted action may have encountered a disposal transaction
        // already owned by this same GL thread and therefore returned
        // reentrantly. The admission token makes this caller a participant in
        // that transaction, so it must join the published outcome rather than
        // taking DisposeCore's intentionally idempotent repeated-call path.
        ExceptionDispatchInfo transactionFailure = null;
        try
        {
            JoinPublishedDeviceDisposal();
        }
        catch (Exception exception)
        {
            transactionFailure = ExceptionDispatchInfo.Capture(exception);
        }

        if (ownsTerminatedThreadJoin)
        {
            // The proxy which caused termination is the only admitted disposal
            // caller that can safely join. Reentrant/concurrent proxies complete
            // while their owning outer worker action is still active.
            executionThread.JoinTerminatedThread();
        }

        if (proxyFailure == null)
        {
            transactionFailure?.Throw();
            return;
        }

        if (transactionFailure == null
            || ReferenceEquals(proxyFailure.SourceException, transactionFailure.SourceException))
        {
            proxyFailure.Throw();
            return;
        }

        throw new AggregateException(
            "OpenGL disposal proxy and owning teardown both failed.",
            proxyFailure.SourceException,
            transactionFailure.SourceException);
    }

    public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
    {
        return _maxColorTextureSamples;
    }

    private protected override bool GetPixelFormatSupportCore(
        PixelFormat format,
        TextureType type,
        TextureUsage usage,
        out PixelFormatProperties properties)
    {
        if (type == TextureType.Texture1D && !_features.Texture1D
            || !OpenGLFormats.IsFormatSupported(_extensions, format, _backendType)
            || (usage & TextureUsage.Staging) != 0
                && IsCompressedStagingReadbackUnsupported(format))
        {
            properties = default(PixelFormatProperties);
            return false;
        }

        uint sampleCounts = 0;
        int max = (int)_maxColorTextureSamples + 1;
        for (int i = 0; i < max; i++)
        {
            sampleCounts |= (uint)(1 << i);
        }

        properties = new PixelFormatProperties(
            _maxTextureSize,
            type == TextureType.Texture1D ? 1 : _maxTextureSize,
            type != TextureType.Texture3D ? 1 : _maxTexDepth,
            uint.MaxValue,
            type == TextureType.Texture3D ? 1 : _maxTexArrayLayers,
            sampleCounts);
        return true;
    }

    protected override MappedResource MapCore(MappableResource resource, MapMode mode, uint subresource)
    {
        if (mode != MapMode.Write
            && resource is OpenGLTexture texture
            && IsCompressedStagingReadbackUnsupported(texture.Format))
        {
            throw new NeoVeldridException(
                "Reading compressed textures through staging resources is not supported by the OpenGL ES backend.");
        }

        MappedResourceCacheKey key = new MappedResourceCacheKey(resource, subresource);
        lock (_mappedResourceLock)
        {
            if (_mappedResources.TryGetValue(key, out MappedResourceInfoWithStaging info))
            {
                if (info.Mode != mode)
                {
                    throw NeoVeldridMappedResourceException.ConflictingMode(resource, subresource);
                }

                info.RefCount += 1;
                _mappedResources[key] = info;
                return info.MappedResource;
            }
        }

        return _executionThread.Map(resource, mode, subresource);
    }

    // OpenGL ES has no equivalent of the desktop glGetCompressedTexImage API.
    private bool IsCompressedStagingReadbackUnsupported(PixelFormat format)
        => _backendType == GraphicsBackend.OpenGLES && FormatHelpers.IsCompressedFormat(format);

    protected override void UnmapCore(MappableResource resource, uint subresource)
    {
        _executionThread.Unmap(resource, subresource);
    }

    internal static void ThrowIfMapped(DeviceBuffer buffer)
    {
        OpenGLBuffer glBuffer = Util.AssertSubtype<DeviceBuffer, OpenGLBuffer>(buffer);
        if (glBuffer.IsMapped)
        {
            throw NeoVeldridMappedResourceException.Mapped(buffer, 0);
        }
    }

    private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
    {
        ThrowIfMapped(buffer);
        StagingBlock sb = _stagingMemoryPool.Stage(source, sizeInBytes);
        _executionThread.UpdateBuffer(buffer, bufferOffsetInBytes, sb);
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
        StagingBlock textureData = _stagingMemoryPool.Stage(source, sizeInBytes);
        StagingBlock argBlock;
        try
        {
            argBlock = _stagingMemoryPool.GetStagingBlock(UpdateTextureArgsSize);
        }
        catch
        {
            _stagingMemoryPool.Free(textureData);
            throw;
        }
        ref UpdateTextureArgs args = ref Unsafe.AsRef<UpdateTextureArgs>(argBlock.Data);
        args.Data = (IntPtr)textureData.Data;
        args.X = x;
        args.Y = y;
        args.Z = z;
        args.Width = width;
        args.Height = height;
        args.Depth = depth;
        args.MipLevel = mipLevel;
        args.ArrayLayer = arrayLayer;

        _executionThread.UpdateTexture(texture, argBlock.Id, textureData.Id);
    }

    private static readonly uint UpdateTextureArgsSize = (uint)Unsafe.SizeOf<UpdateTextureArgs>();

    private struct UpdateTextureArgs
    {
        public IntPtr Data;
        public uint X;
        public uint Y;
        public uint Z;
        public uint Width;
        public uint Height;
        public uint Depth;
        public uint MipLevel;
        public uint ArrayLayer;
    }

    public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
    {
        ThrowIfExecutionThreadWouldBlock("wait for an OpenGL fence", nanosecondTimeout);
        return Util.AssertSubtype<Fence, OpenGLFence>(fence).Wait(nanosecondTimeout);
    }

    public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
    {
        ThrowIfExecutionThreadWouldBlock("wait for OpenGL fences", nanosecondTimeout);

        int msTimeout;
        if (nanosecondTimeout == ulong.MaxValue)
        {
            msTimeout = -1;
        }
        else
        {
            msTimeout = (int)Math.Min(nanosecondTimeout / 1_000_000, int.MaxValue);
        }

        ManualResetEvent[] events = GetResetEventArray(fences.Length);
        int acquiredCount = 0;
        try
        {
            for (int i = 0; i < fences.Length; i++)
            {
                OpenGLFence glFence = Util.AssertSubtype<Fence, OpenGLFence>(fences[i]);
                events[i] = glFence.AcquireWaitHandle();
                acquiredCount++;
            }

            bool result;
            if (waitAll)
            {
                result = WaitHandle.WaitAll(events, msTimeout);
            }
            else
            {
                int index = WaitHandle.WaitAny(events, msTimeout);
                result = index != WaitHandle.WaitTimeout;
            }

            if (result)
            {
                for (int i = 0; i < fences.Length; i++)
                {
                    Util.AssertSubtype<Fence, OpenGLFence>(fences[i])
                        .ThrowSubmissionExceptionIfSignaled();
                }
            }

            return result;
        }
        finally
        {
            for (int i = 0; i < acquiredCount; i++)
            {
                Util.AssertSubtype<Fence, OpenGLFence>(fences[i]).ReleaseWaitHandle();
                events[i] = null;
            }
            ReturnResetEventArray(events);
        }
    }

    private ManualResetEvent[] GetResetEventArray(int length)
    {
        lock (_resetEventsLock)
        {
            for (int i = _resetEvents.Count - 1; i >= 0; i--)
            {
                ManualResetEvent[] array = _resetEvents[i];
                if (array.Length == length)
                {
                    _resetEvents.RemoveAt(i);
                    return array;
                }
            }
        }

        ManualResetEvent[] newArray = new ManualResetEvent[length];
        return newArray;
    }

    private void ReturnResetEventArray(ManualResetEvent[] array)
    {
        lock (_resetEventsLock)
        {
            _resetEvents.Add(array);
        }
    }

    public override void ResetFence(Fence fence)
    {
        Util.AssertSubtype<Fence, OpenGLFence>(fence).Reset();
    }

    private void ThrowIfExecutionThreadWouldBlock(string operation, ulong nanosecondTimeout)
    {
        if (nanosecondTimeout != 0 && _executionThread?.IsExecutionThread == true)
        {
            throw new InvalidOperationException(
                $"The OpenGL execution thread cannot {operation}; blocking it would prevent "
                + "queued submissions from reaching their completion boundary. Use a zero timeout "
                + "for a non-blocking query or wait from another thread.");
        }
    }

    internal void EnqueueDisposal(OpenGLDeferredResource resource)
    {
        _resourcesToDispose.Enqueue(resource);
    }

    internal void EnqueueDisposal(OpenGLCommandList commandList)
    {
        lock (_commandListDisposalLock)
        {
            if (GetCount(commandList) > 0)
            {
                _commandListsToDispose.Add(commandList);
            }
            else
            {
                commandList.DestroyResources();
            }
        }
    }

    internal bool CheckCommandListDisposal(OpenGLCommandList commandList)
    {

        lock (_commandListDisposalLock)
        {
            int count = DecrementCount(commandList);
            if (count == 0)
            {
                if (_commandListsToDispose.Remove(commandList))
                {
                    commandList.DestroyResources();
                    return true;
                }
            }

            return false;
        }
    }

    private void FlushDisposables()
    {
        if (_glContextUnavailable) return;

        while (_resourcesToDispose.TryDequeue(out OpenGLDeferredResource resource))
        {
            resource.DestroyGLResources();
        }
    }

    internal void InsertValidationTestMessage(DebugSeverity severity, uint id, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_debugOutput == null || !Validation.Status.IsActive)
        {
            throw new InvalidOperationException("OpenGL debug output is not active.");
        }

        ExecuteOnGLThread(() => _debugOutput.InsertTestMessage(severity, id, text));
    }

    protected override void PlatformDispose()
    {
        List<Exception> failures = null;
        if (_executionThread == null)
        {
            DisposePartiallyConstructedContext(ref failures);
        }
        else
        {
            DisposeExecutionThreadContext(ref failures);
        }

        if (_debugOutput?.IsCallbackRootRetained == true)
        {
            string ownershipFailure = _glContextDestructionConfirmed
                ? "The OpenGL context was confirmed destroyed, but its debug callback root "
                    + "remained retained. This violates the callback ownership invariant."
                : "The OpenGL debug callback could not be safely released because neither "
                    + "native unregistration nor context destruction was confirmed. Its managed "
                    + "callback root has been deliberately retained.";
            AddPlatformCleanupFailure(
                new NeoVeldridException(ownershipFailure),
                ref failures);
        }
        else
        {
            _debugOutput = null;
        }
        _glContext = IntPtr.Zero;
        _glContextUnavailable = true;
        AttemptPlatformCleanup(
            () => _workItems?.Dispose(),
            ignoreContextUnavailable: false,
            ref failures);

        ThrowPlatformCleanupFailures(failures);
    }

    private void DisposeExecutionThreadContext(ref List<Exception> failures)
    {
        AttemptPlatformCleanup(
            FlushAndFinish,
            ignoreContextUnavailable: true,
            ref failures);
        if (_debugOutput != null)
        {
            AttemptPlatformCleanup(
                () => ExecuteOnGLThread(_debugOutput.Unregister),
                ignoreContextUnavailable: false,
                ref failures);
        }

        // Termination owns context deletion and must run even when flushing,
        // callback removal, or a previously queued command failed.
        AttemptPlatformCleanup(
            _executionThread.Terminate,
            ignoreContextUnavailable: false,
            ref failures);
        AttemptPlatformCleanup(
            _stagingMemoryPool.Dispose,
            ignoreContextUnavailable: false,
            ref failures);
    }

    private void DisposePartiallyConstructedContext(ref List<Exception> failures)
    {
        bool contextAvailable = _glContext != IntPtr.Zero && !_glContextUnavailable;
        bool contextCurrent = false;
        if (contextAvailable)
        {
            contextCurrent = AttemptPlatformCleanup(
                () =>
                {
                    _makeCurrent(_glContext);
                    IntPtr currentContext = _getCurrentContext();
                    if (currentContext != _glContext)
                    {
                        throw new NeoVeldridException(
                            "The platform did not make the owned OpenGL context current during teardown.");
                    }
                },
                ignoreContextUnavailable: false,
                ref failures);
        }

        if (contextCurrent)
        {
            AttemptPlatformCleanup(
                FlushDisposables,
                ignoreContextUnavailable: true,
                ref failures);
        }

        if (contextCurrent && _debugOutput != null)
        {
            AttemptPlatformCleanup(
                _debugOutput.Unregister,
                ignoreContextUnavailable: false,
                ref failures);
        }

        if (contextCurrent)
        {
            AttemptPlatformCleanup(
                () => ClearOwnedContext("partial device teardown"),
                ignoreContextUnavailable: false,
                ref failures);
        }

        AttemptPlatformCleanup(
            _stagingMemoryPool.Dispose,
            ignoreContextUnavailable: false,
            ref failures);
        if (_glContext != IntPtr.Zero)
        {
            AttemptPlatformCleanup(
                () =>
                {
                    _deleteContext(_glContext);
                    ConfirmContextDestroyed();
                },
                ignoreContextUnavailable: false,
                ref failures);
        }
    }

    private static bool AttemptPlatformCleanup(
        Action cleanup,
        bool ignoreContextUnavailable,
        ref List<Exception> failures)
    {
        try
        {
            cleanup();
            return true;
        }
        catch (Exception exception)
        {
            if (ignoreContextUnavailable && IsContextUnavailable(exception))
            {
                return false;
            }

            AddPlatformCleanupFailure(exception, ref failures);
            return false;
        }
    }

    private static void AddPlatformCleanupFailure(Exception exception, ref List<Exception> failures)
    {
        failures ??= new List<Exception>();
        failures.Add(exception);
    }

    private void ConfirmContextDestroyed()
    {
        _glContextDestructionConfirmed = true;
        _glContextUnavailable = true;
        _debugOutput?.ConfirmContextDestroyed();
    }

    private static bool IsContextUnavailable(Exception exception)
    {
        if (exception is SymbolLoadingException)
        {
            return true;
        }

        if (exception is AggregateException aggregateException)
        {
            if (aggregateException.InnerExceptions.Count == 0)
            {
                return false;
            }

            foreach (Exception innerException in aggregateException.InnerExceptions)
            {
                if (!IsContextUnavailable(innerException))
                {
                    return false;
                }
            }
            return true;
        }

        return exception.InnerException != null
            && IsContextUnavailable(exception.InnerException);
    }

    private static void ThrowPlatformCleanupFailures(List<Exception> failures)
    {
        if (failures == null || failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(
            "OpenGL teardown encountered multiple failures.",
            failures);
    }

    public override bool GetOpenGLInfo(out BackendInfoOpenGL info)
    {
        info = _openglInfo;
        return true;
    }

    internal void ExecuteOnGLThread(Action action)
    {
        _executionThread.Run(action);
    }

    internal override int PendingBackendWorkItemCount =>
        _executionThread?.PendingWorkItemCount ?? 0;

    internal void FlushAndFinish()
    {
        _executionThread.FlushAndFinish();
    }

    internal void EnsureResourceInitialized(OpenGLDeferredResource deferredResource)
    {
        _executionThread.InitializeResource(deferredResource);
    }

    internal override uint GetUniformBufferMinOffsetAlignmentCore() => _minUboOffsetAlignment;

    internal override uint GetStructuredBufferMinOffsetAlignmentCore() => _minSsboOffsetAlignment;

    private class ExecutionThread
    {
        private readonly OpenGLGraphicsDevice _gd;
        private readonly BlockingCollection<ExecutionThreadWorkItem> _workItems;
        private readonly Queue<ExecutionThreadWorkItem> _deferredOrderedWorkItems =
            new Queue<ExecutionThreadWorkItem>();
        private readonly HashSet<WorkItemCompletion> _activeSnapshotBoundaries =
            new HashSet<WorkItemCompletion>();
        private readonly Action<IntPtr> _makeCurrent;
        private readonly Func<IntPtr> _getCurrentContext;
        private readonly IntPtr _context;
        private readonly Thread _thread;
        private readonly object _admissionLock = new object();
        private ExecutionThreadState _state = ExecutionThreadState.Running;
        private bool _started;
        private bool _terminated;
        private readonly List<Exception> _exceptions = new List<Exception>();
        private readonly object _exceptionsLock = new object();
        private Exception _startupException;
        private Exception _terminationException;
        private int _synchronousWorkItemDepth;
        private int _gpuWorkItemDepth;
        private int _deferredOrderedWorkItemCount;

        public ExecutionThread(
            OpenGLGraphicsDevice gd,
            BlockingCollection<ExecutionThreadWorkItem> workItems,
            Action<IntPtr> makeCurrent,
            Func<IntPtr> getCurrentContext,
            IntPtr context)
        {
            _gd = gd;
            _workItems = workItems;
            _makeCurrent = makeCurrent;
            _getCurrentContext = getCurrentContext;
            _context = context;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Start();

            lock (_admissionLock)
            {
                while (!_started)
                {
                    Monitor.Wait(_admissionLock);
                }
            }
            if (_startupException != null)
            {
                _thread.Join();
                throw new NeoVeldridException(
                    "The OpenGL execution thread could not acquire its context.",
                    _startupException);
            }
        }

        private void Run()
        {
            try
            {
                _makeCurrent(_context);
                IntPtr currentContext = _getCurrentContext();
                if (currentContext != _context)
                {
                    throw new NeoVeldridException(
                        "The platform did not make the owned OpenGL context current on the execution thread. "
                        + $"Expected 0x{_context.ToInt64():X}, but observed 0x{currentContext.ToInt64():X}.");
                }
            }
            catch (Exception exception)
            {
                lock (_admissionLock)
                {
                    _startupException = exception;
                    _terminated = true;
                    _state = ExecutionThreadState.Closed;
                    _workItems.CompleteAdding();
                    Monitor.PulseAll(_admissionLock);
                }
            }
            finally
            {
                lock (_admissionLock)
                {
                    _started = true;
                    Monitor.PulseAll(_admissionLock);
                }
            }

            while (!_terminated)
            {
                if (!TryTakeNextWorkItem(out ExecutionThreadWorkItem workItem))
                {
                    break;
                }
                ExecuteWorkItem(workItem);
            }

            if (!_terminated)
            {
                FailExecutionThread(
                    new NeoVeldridException(
                        "The OpenGL execution queue completed without a termination work item."));
            }
        }

        internal bool IsExecutionThread => Thread.CurrentThread == _thread;

        internal bool IsExecutingSynchronousWorkItem =>
            IsExecutionThread && _synchronousWorkItemDepth != 0;

        private bool IsExecutingGpuWork =>
            IsExecutionThread && _gpuWorkItemDepth != 0;

        internal int PendingWorkItemCount =>
            _workItems.Count + Volatile.Read(ref _deferredOrderedWorkItemCount);

        internal void ThrowIfExecutingGpuWork(string operation)
        {
            if (IsExecutingGpuWork)
            {
                throw new InvalidOperationException(
                    $"The OpenGL execution thread cannot {operation} from inside submitted GPU work. "
                    + "The active command or update must return before a lifecycle boundary can begin.");
            }
        }

        private bool TryTakeNextWorkItem(out ExecutionThreadWorkItem workItem)
        {
            if (TryTakeDeferredOrderedWorkItem(out workItem))
            {
                return true;
            }

            try
            {
                workItem = _workItems.Take();
                return true;
            }
            catch (InvalidOperationException) when (_workItems.IsCompleted)
            {
                workItem = default;
                return false;
            }
        }

        private bool TryTakeAcceptedWork(out ExecutionThreadWorkItem workItem)
        {
            if (TryTakeDeferredOrderedWorkItem(out workItem))
            {
                return true;
            }

            return _workItems.TryTake(out workItem);
        }

        private bool TryTakeDeferredOrderedWorkItem(out ExecutionThreadWorkItem workItem)
        {
            if (_deferredOrderedWorkItems.Count == 0)
            {
                workItem = default;
                return false;
            }

            workItem = _deferredOrderedWorkItems.Dequeue();
            Interlocked.Decrement(ref _deferredOrderedWorkItemCount);
            return true;
        }

        private void DeferOrderedWorkItem(ExecutionThreadWorkItem workItem)
        {
            _deferredOrderedWorkItems.Enqueue(workItem);
            Interlocked.Increment(ref _deferredOrderedWorkItemCount);
        }

        private void QueueWorkItem(ExecutionThreadWorkItem workItem)
        {
            lock (_admissionLock)
            {
                if (_state != ExecutionThreadState.Running)
                {
                    throw new ObjectDisposedException(
                        nameof(OpenGLGraphicsDevice),
                        "The OpenGL execution thread is no longer accepting work.");
                }

                _workItems.Add(workItem);
            }
        }

        private void ExecuteWorkItem(ExecutionThreadWorkItem workItem)
        {
            Exception failure = null;
            bool isSynchronousWorkItem = workItem.Completion != null;
            bool isGpuWorkItem = workItem.IsGpuWork;
            if (isSynchronousWorkItem)
            {
                _synchronousWorkItemDepth++;
            }
            if (isGpuWorkItem)
            {
                _gpuWorkItemDepth++;
            }

            try
            {
                switch (workItem.Type)
                {
                    case WorkItemType.ExecuteList:
                    {
                        OpenGLCommandEntryList list = (OpenGLCommandEntryList)workItem.Object0;
                        OpenGLFence fence = (OpenGLFence)workItem.Object1;
                        List<Exception> failures = null;
                        try
                        {
                            list.ExecuteAll(_gd._commandExecutor);
                        }
                        catch (Exception exception)
                        {
                            AddFailure(exception, ref failures);
                        }

                        if (fence != null || workItem.Completion != null)
                        {
                            try
                            {
                                // OpenGLFence is a CPU event, so it is only truthful after
                                // all commands issued by this list have completed on the GPU.
                                // Validation boundaries also wait here so asynchronous driver
                                // failures are observable before their checkpoint runs.
                                _gd.GL.Flush();
                                _gd.GL.Finish();
                            }
                            catch (Exception exception)
                            {
                                AddFailure(exception, ref failures);
                            }
                        }

                        try
                        {
                            if (!_gd.CheckCommandListDisposal(list.Parent))
                            {
                                list.Parent.OnCompleted(list);
                            }
                        }
                        catch (Exception exception)
                        {
                            AddFailure(exception, ref failures);
                        }

                        Exception commandFailure = CreateFailure(
                            "OpenGL command execution encountered multiple failures.",
                            failures);
                        if (fence != null)
                        {
                            try
                            {
                                fence.CompleteSubmission(commandFailure);
                            }
                            catch (Exception exception)
                            {
                                AddFailure(exception, ref failures);
                            }
                        }

                        ThrowFailures(
                            "OpenGL command execution encountered multiple failures.",
                            failures);
                    }
                    break;
                    case WorkItemType.Map:
                    {
                        MappableResource resourceToMap = (MappableResource)workItem.Object0;
                        MapParams* resultPtr = (MapParams*)Util.UnpackIntPtr(workItem.UInt0, workItem.UInt1);

                        if (resultPtr->Map)
                        {
                            ExecuteMapResource(resourceToMap, resultPtr);
                        }
                        else
                        {
                            ExecuteUnmapResource(resourceToMap, resultPtr->Subresource);
                        }
                    }
                    break;
                    case WorkItemType.UpdateBuffer:
                    {
                        DeviceBuffer updateBuffer = (DeviceBuffer)workItem.Object0;
                        uint offsetInBytes = workItem.UInt0;
                        StagingBlock stagingBlock = _gd.StagingMemoryPool.RetrieveById(workItem.UInt1);

                        try
                        {
                            _gd._commandExecutor.UpdateBuffer(
                                updateBuffer,
                                offsetInBytes,
                                (IntPtr)stagingBlock.Data,
                                stagingBlock.SizeInBytes);
                        }
                        finally
                        {
                            _gd.StagingMemoryPool.Free(stagingBlock);
                        }
                    }
                    break;
                    case WorkItemType.UpdateTexture:
                    {
                        Texture texture = (Texture)workItem.Object0;
                        StagingMemoryPool pool = _gd.StagingMemoryPool;
                        StagingBlock argBlock = pool.RetrieveById(workItem.UInt0);
                        StagingBlock textureData = pool.RetrieveById(workItem.UInt1);
                        ref UpdateTextureArgs args = ref Unsafe.AsRef<UpdateTextureArgs>(argBlock.Data);

                        try
                        {
                            _gd._commandExecutor.UpdateTexture(
                                texture, args.Data, args.X, args.Y, args.Z,
                                args.Width, args.Height, args.Depth, args.MipLevel, args.ArrayLayer);
                        }
                        finally
                        {
                            pool.Free(argBlock);
                            pool.Free(textureData);
                        }
                    }
                        break;
                    case WorkItemType.GenericAction:
                    case WorkItemType.DisposalAction:
                    {
                        ((Action)workItem.Object0)();
                    }
                    break;
                    case WorkItemType.TerminateAction:
                    {
                        ExecuteTermination();
                    }
                    break;
                    case WorkItemType.SetSyncToVerticalBlank:
                    {
                        bool value = workItem.UInt0 == 1 ? true : false;
                        _gd._setSyncToVBlank(value);
                    }
                    break;
                    case WorkItemType.SwapBuffers:
                    {
                        _gd._swapBuffers();
                        _gd.FlushDisposables();
                    }
                    break;
                    case WorkItemType.WaitForIdle:
                    {
                        List<Exception> failures = null;
                        try
                        {
                            _gd.FlushDisposables();
                        }
                        catch (Exception exception)
                        {
                            AddFailure(exception, ref failures);
                        }

                        bool isFullFlush = workItem.UInt0 != 0;
                        if (isFullFlush)
                        {
                            try
                            {
                                _gd.GL.Flush();
                                _gd.GL.Finish();
                            }
                            catch (Exception exception)
                            {
                                AddFailure(exception, ref failures);
                            }
                        }

                        ThrowFailures(
                            "OpenGL idle work encountered multiple failures.",
                            failures);
                    }
                    break;
                    case WorkItemType.InitializeResource:
                    {
                        ((OpenGLDeferredResource)workItem.Object0).EnsureResourcesCreated();
                    }
                    break;
                    default:
                        throw new InvalidOperationException("Invalid command type: " + workItem.Type);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (isGpuWorkItem)
                {
                    _gpuWorkItemDepth--;
                }
                if (isSynchronousWorkItem)
                {
                    List<Exception> failures = null;
                    AddFailure(TakePendingException(), ref failures);
                    AddFailure(failure, ref failures);
                    failure = CreateFailure(
                        "OpenGL synchronous work encountered multiple failures.",
                        failures);
                    _synchronousWorkItemDepth--;
                    workItem.Completion.Complete(failure, _terminated);
                }
                else if (failure != null)
                {
                    AddPendingException(failure);
                }
            }
        }

        private void AddPendingException(Exception exception)
        {
            lock (_exceptionsLock)
            {
                _exceptions.Add(exception);
            }
        }

        private void ExecuteMapResource(
            MappableResource resource,
            MapParams* result)
        {
            uint subresource = result->Subresource;
            MapMode mode = result->MapMode;

            MappedResourceCacheKey key = new MappedResourceCacheKey(resource, subresource);
            try
            {
                lock (_gd._mappedResourceLock)
                {
                    Debug.Assert(!_gd._mappedResources.ContainsKey(key));
                    if (resource is OpenGLBuffer buffer)
                    {
                        buffer.EnsureResourcesCreated();
                        void* mappedPtr;
                        MapBufferAccessMask accessMask = OpenGLFormats.VdToGLMapMode(mode);
                        if (_gd.Extensions.ARB_DirectStateAccess)
                        {
                            mappedPtr = _gd.GL.MapNamedBufferRange(buffer.Buffer, IntPtr.Zero, buffer.SizeInBytes, accessMask);
                            CheckLastError();
                        }
                        else
                        {
                            _gd.GL.BindBuffer(BufferTargetARB.CopyWriteBuffer, buffer.Buffer);
                            CheckLastError();

                            mappedPtr = _gd.GL.MapBufferRange(BufferTargetARB.CopyWriteBuffer, IntPtr.Zero, (UIntPtr)buffer.SizeInBytes, accessMask);
                            CheckLastError();
                        }

                        MappedResourceInfoWithStaging info = new MappedResourceInfoWithStaging();
                        info.MappedResource = new MappedResource(
                            resource,
                            mode,
                            (IntPtr)mappedPtr,
                            buffer.SizeInBytes);
                        info.RefCount = 1;
                        info.Mode = mode;
                        _gd._mappedResources.Add(key, info);
                        buffer.IsMapped = true;
                        result->Data = (IntPtr)mappedPtr;
                        result->DataSize = buffer.SizeInBytes;
                        result->RowPitch = 0;
                        result->DepthPitch = 0;
                        result->Succeeded = true;
                    }
                    else
                    {
                        OpenGLTexture texture = Util.AssertSubtype<MappableResource, OpenGLTexture>(resource);
                        texture.EnsureResourcesCreated();

                        Util.GetMipLevelAndArrayLayer(texture, subresource, out uint mipLevel, out uint arrayLayer);
                        Util.GetMipDimensions(texture, mipLevel, out uint mipWidth, out uint mipHeight, out uint mipDepth);

                        uint depthSliceSize = FormatHelpers.GetDepthPitch(
                            FormatHelpers.GetRowPitch(mipWidth, texture.Format),
                            mipHeight,
                            texture.Format);
                        uint subresourceSize = depthSliceSize * mipDepth;
                        int compressedSize = 0;

                        bool isCompressed = FormatHelpers.IsCompressedFormat(texture.Format);
                        if (isCompressed)
                        {
                            _gd.GL.GetTexLevelParameter(
                                texture.TextureTarget,
                                (int)mipLevel,
                                (GetTextureParameter)GLEnum.TextureCompressedImageSize,
                                out compressedSize);
                            CheckLastError();
                        }

                        StagingBlock block = _gd._stagingMemoryPool.GetStagingBlock(subresourceSize);

                        uint packAlignment = 4;
                        if (!isCompressed)
                        {
                            packAlignment = FormatSizeHelpers.GetSizeInBytes(texture.Format);
                        }

                        if (packAlignment < 4)
                        {
                            _gd.GL.PixelStore(PixelStoreParameter.PackAlignment, (int)packAlignment);
                            CheckLastError();
                        }

                        if (mode == MapMode.Read || mode == MapMode.ReadWrite)
                        {
                            if (!isCompressed)
                            {
                                // Read data into buffer.
                                if (_gd.Extensions.ARB_DirectStateAccess && texture.ArrayLayers == 1)
                                {
                                    int zoffset = texture.ArrayLayers > 1 ? (int)arrayLayer : 0;
                                    _gd.GL.GetTextureSubImage(
                                        texture.Texture,
                                        (int)mipLevel,
                                        0, 0, zoffset,
                                        mipWidth, mipHeight, mipDepth,
                                        texture.GLPixelFormat,
                                        texture.GLPixelType,
                                        subresourceSize,
                                        block.Data);
                                    CheckLastError();
                                }
                                else
                                {
                                    for (uint layer = 0; layer < mipDepth; layer++)
                                    {
                                        uint curLayer = arrayLayer + layer;
                                        uint curOffset = depthSliceSize * layer;
                                        uint readFB = _gd.GL.GenFramebuffer();
                                        CheckLastError();
                                        _gd.GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFB);
                                        CheckLastError();

                                        if (texture.ArrayLayers > 1 || texture.Type == TextureType.Texture3D)
                                        {
                                            _gd.GL.FramebufferTextureLayer(
                                                FramebufferTarget.ReadFramebuffer,
                                                GLFramebufferAttachment.ColorAttachment0,
                                                texture.Texture,
                                                (int)mipLevel,
                                                (int)curLayer);
                                            CheckLastError();
                                        }
                                        else if (texture.Type == TextureType.Texture1D)
                                        {
                                            _gd.GL.FramebufferTexture1D(
                                                FramebufferTarget.ReadFramebuffer,
                                                GLFramebufferAttachment.ColorAttachment0,
                                                TextureTarget.Texture1D,
                                                texture.Texture,
                                                (int)mipLevel);
                                            CheckLastError();
                                        }
                                        else
                                        {
                                            _gd.GL.FramebufferTexture2D(
                                                FramebufferTarget.ReadFramebuffer,
                                                GLFramebufferAttachment.ColorAttachment0,
                                                TextureTarget.Texture2D,
                                                texture.Texture,
                                                (int)mipLevel);
                                            CheckLastError();
                                        }

                                        _gd.GL.ReadPixels(
                                            0, 0,
                                            mipWidth, mipHeight,
                                            texture.GLPixelFormat,
                                            texture.GLPixelType,
                                            (byte*)block.Data + curOffset);
                                        CheckLastError();
                                        _gd.GL.DeleteFramebuffer(readFB);
                                        CheckLastError();
                                    }
                                }
                            }
                            else // isCompressed
                            {
                                if (texture.TextureTarget == TextureTarget.Texture2DArray
                                    || texture.TextureTarget == TextureTarget.Texture2DMultisampleArray
                                    || texture.TextureTarget == TextureTarget.TextureCubeMapArray)
                                {
                                    // We only want a single subresource (array slice), so we need to copy
                                    // a subsection of the downloaded data into our staging block.

                                    uint fullDataSize = (uint)compressedSize;
                                    StagingBlock fullBlock = _gd._stagingMemoryPool.GetStagingBlock(fullDataSize);

                                    if (_gd.Extensions.ARB_DirectStateAccess)
                                    {
                                        _gd.GL.GetCompressedTextureImage(
                                            texture.Texture,
                                            (int)mipLevel,
                                            fullBlock.SizeInBytes,
                                            fullBlock.Data);
                                        CheckLastError();
                                    }
                                    else
                                    {
                                        _gd.TextureSamplerManager.SetTextureTransient(texture.TextureTarget, texture.Texture);
                                        CheckLastError();

                                        _gd.GL.GetCompressedTexImage(texture.TextureTarget, (int)mipLevel, fullBlock.Data);
                                        CheckLastError();
                                    }
                                    byte* sliceStart = (byte*)fullBlock.Data + (arrayLayer * subresourceSize);
                                    System.Buffer.MemoryCopy(sliceStart, block.Data, subresourceSize, subresourceSize);
                                    _gd._stagingMemoryPool.Free(fullBlock);
                                }
                                else
                                {
                                    if (_gd.Extensions.ARB_DirectStateAccess)
                                    {
                                        _gd.GL.GetCompressedTextureImage(
                                            texture.Texture,
                                            (int)mipLevel,
                                            block.SizeInBytes,
                                            block.Data);
                                        CheckLastError();
                                    }
                                    else
                                    {
                                        _gd.TextureSamplerManager.SetTextureTransient(texture.TextureTarget, texture.Texture);
                                        CheckLastError();

                                        _gd.GL.GetCompressedTexImage(texture.TextureTarget, (int)mipLevel, block.Data);
                                        CheckLastError();
                                    }
                                }
                            }
                        }

                        if (packAlignment < 4)
                        {
                            _gd.GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
                            CheckLastError();
                        }

                        uint rowPitch = FormatHelpers.GetRowPitch(mipWidth, texture.Format);
                        uint depthPitch = FormatHelpers.GetDepthPitch(rowPitch, mipHeight, texture.Format);
                        MappedResourceInfoWithStaging info = new MappedResourceInfoWithStaging();
                        info.MappedResource = new MappedResource(
                            resource,
                            mode,
                            (IntPtr)block.Data,
                            subresourceSize,
                            subresource,
                            rowPitch,
                            depthPitch);
                        info.RefCount = 1;
                        info.Mode = mode;
                        info.StagingBlock = block;
                        _gd._mappedResources.Add(key, info);
                        result->Data = (IntPtr)block.Data;
                        result->DataSize = subresourceSize;
                        result->RowPitch = rowPitch;
                        result->DepthPitch = depthPitch;
                        result->Succeeded = true;
                    }
                }
            }
            catch
            {
                result->Succeeded = false;
                throw;
            }
        }

        private void ExecuteUnmapResource(MappableResource resource, uint subresource)
        {
            MappedResourceCacheKey key = new MappedResourceCacheKey(resource, subresource);
            lock (_gd._mappedResourceLock)
            {
                MappedResourceInfoWithStaging info = _gd._mappedResources[key];
                if (info.RefCount == 1)
                {
                    if (resource is OpenGLBuffer buffer)
                    {
                        if (_gd.Extensions.ARB_DirectStateAccess)
                        {
                            _gd.GL.UnmapNamedBuffer(buffer.Buffer);
                            CheckLastError();
                        }
                        else
                        {
                            _gd.GL.BindBuffer(BufferTargetARB.CopyWriteBuffer, buffer.Buffer);
                            CheckLastError();

                            _gd.GL.UnmapBuffer(BufferTargetARB.CopyWriteBuffer);
                            CheckLastError();
                        }

                        buffer.IsMapped = false;
                    }
                    else
                    {
                        OpenGLTexture texture = Util.AssertSubtype<MappableResource, OpenGLTexture>(resource);

                        if (info.Mode == MapMode.Write || info.Mode == MapMode.ReadWrite)
                        {
                            Util.GetMipLevelAndArrayLayer(texture, subresource, out uint mipLevel, out uint arrayLayer);
                            Util.GetMipDimensions(texture, mipLevel, out uint width, out uint height, out uint depth);

                            IntPtr data = (IntPtr)info.StagingBlock.Data;

                            _gd._commandExecutor.UpdateTexture(
                                texture,
                                data,
                                0, 0, 0,
                                width, height, depth,
                                mipLevel,
                                arrayLayer);
                        }

                        _gd.StagingMemoryPool.Free(info.StagingBlock);
                    }

                    _gd._mappedResources.Remove(key);
                }
            }

        }

        private void CheckExceptions()
        {
            Exception exception = TakePendingException();
            if (exception != null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        private Exception TakePendingException()
        {
            lock (_exceptionsLock)
            {
                if (_exceptions.Count == 0)
                {
                    return null;
                }

                Exception innerException = _exceptions.Count == 1
                    ? _exceptions[0]
                    : new AggregateException(_exceptions.ToArray());
                _exceptions.Clear();
                return new NeoVeldridException(
                    "Error(s) were encountered during the execution of OpenGL commands. See InnerException for more information.",
                    innerException);
            }
        }

        public MappedResource Map(MappableResource resource, MapMode mode, uint subresource)
        {
            MapParams mrp = new MapParams();
            mrp.Map = true;
            mrp.Subresource = subresource;
            mrp.MapMode = mode;

            WorkItemCompletion completion = new WorkItemCompletion();
            ExecuteSynchronously(new ExecutionThreadWorkItem(resource, &mrp, completion));
            if (!mrp.Succeeded)
            {
                throw NeoVeldridMappedResourceException.MapFailed(resource, subresource);
            }

            return new MappedResource(resource, mode, mrp.Data, mrp.DataSize, mrp.Subresource, mrp.RowPitch, mrp.DepthPitch);
        }

        internal void Unmap(MappableResource resource, uint subresource)
        {
            MapParams mrp = new MapParams();
            mrp.Map = false;
            mrp.Subresource = subresource;

            ExecuteSynchronously(
                new ExecutionThreadWorkItem(
                    resource,
                    &mrp,
                    new WorkItemCompletion()));
        }

        public void ExecuteCommands(
            OpenGLCommandEntryList entryList,
            OpenGLFence fence,
            bool waitForNativeCompletion)
        {
            ThrowIfExecutingGpuWork("submit another command list");
            WorkItemCompletion completion = waitForNativeCompletion
                ? new WorkItemCompletion()
                : null;
            ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(
                entryList,
                fence,
                completion);
            bool executeInline = IsExecutionThread;
            bool submitted = false;
            bool countIncremented = false;
            bool fencePrepared = false;

            lock (_gd._commandListDisposalLock)
            {
                try
                {
                    fence?.BeginSubmission();
                    fencePrepared = fence != null;
                    _gd.IncrementCount(entryList.Parent);
                    countIncremented = true;
                    submitted = true;
                    entryList.Parent.OnSubmitted(entryList);
                    if (executeInline)
                    {
                        EnsureAdmissionOpen();
                    }
                    else
                    {
                        QueueWorkItem(workItem);
                    }
                }
                catch (Exception admissionFailure)
                {
                    List<Exception> failures = null;
                    AddFailure(admissionFailure, ref failures);
                    try
                    {
                        if (countIncremented && submitted)
                        {
                            if (!_gd.CheckCommandListDisposal(entryList.Parent))
                            {
                                entryList.Parent.OnCompleted(entryList);
                            }
                        }
                        else if (countIncremented)
                        {
                            _gd.DecrementCount(entryList.Parent);
                        }
                    }
                    catch (Exception ownershipFailure)
                    {
                        AddFailure(ownershipFailure, ref failures);
                    }

                    if (fencePrepared)
                    {
                        try
                        {
                            fence.CancelSubmission();
                        }
                        catch (Exception fenceFailure)
                        {
                            AddFailure(fenceFailure, ref failures);
                        }
                    }
                    completion?.Dispose();

                    ThrowFailures(
                        "OpenGL command submission was rejected and ownership rollback also failed.",
                        failures);
                }
            }

            if (executeInline)
            {
                ExecuteWorkItem(workItem);
                if (completion == null)
                {
                    CheckExceptions();
                }
            }

            completion?.WaitAndThrow();
        }

        internal void UpdateBuffer(DeviceBuffer buffer, uint offsetInBytes, StagingBlock stagingBlock)
        {
            ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(
                buffer,
                offsetInBytes,
                stagingBlock);
            bool ownershipTransferred = false;
            try
            {
                DispatchAsynchronous(workItem, out ownershipTransferred);
            }
            catch
            {
                if (!ownershipTransferred)
                {
                    _gd.StagingMemoryPool.Free(stagingBlock);
                }
                throw;
            }
        }

        internal void UpdateTexture(Texture texture, uint argBlockId, uint dataBlockId)
        {
            ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(
                texture,
                argBlockId,
                dataBlockId);
            bool ownershipTransferred = false;
            try
            {
                DispatchAsynchronous(workItem, out ownershipTransferred);
            }
            catch
            {
                if (!ownershipTransferred)
                {
                    StagingMemoryPool pool = _gd.StagingMemoryPool;
                    pool.Free(pool.RetrieveById(argBlockId));
                    pool.Free(pool.RetrieveById(dataBlockId));
                }
                throw;
            }
        }

        internal void Run(Action a)
        {
            ArgumentNullException.ThrowIfNull(a);
            if (IsExecutingGpuWork)
            {
                // ExecuteOnGLThread is already on its promised thread. Keeping
                // the nested action inside the active command preserves command
                // atomicity; lifecycle calls made by that action are rejected by
                // the GPU-work guard above.
                a();
                return;
            }

            ExecuteBoundaryWork(
                new ExecutionThreadWorkItem(
                    a,
                    new WorkItemCompletion()),
                "OpenGL execution-thread action encountered multiple failures.");
        }

        internal bool TryRunDisposal(
            Action disposeCore,
            out ExceptionDispatchInfo completionFailure,
            out bool ownsTerminatedThreadJoin)
        {
            ArgumentNullException.ThrowIfNull(disposeCore);
            Debug.Assert(!IsExecutionThread);
            completionFailure = null;
            ownsTerminatedThreadJoin = false;

            WorkItemCompletion completion = new WorkItemCompletion();
            lock (_admissionLock)
            {
                if (_state != ExecutionThreadState.Running)
                {
                    completion.Dispose();
                    return false;
                }

                try
                {
                    _workItems.Add(
                        ExecutionThreadWorkItem.CreateDisposal(
                            disposeCore,
                            completion));
                }
                catch
                {
                    completion.Dispose();
                    throw;
                }
            }

            // Once admitted, the work item's exact teardown failure belongs to
            // this caller. A false result is reserved for pre-admission closure.
            try
            {
                completion.WaitAndThrow();
            }
            catch (Exception exception)
            {
                completionFailure = ExceptionDispatchInfo.Capture(exception);
            }
            ownsTerminatedThreadJoin = completion.ExecutionThreadTerminatedAtCompletion;
            return true;
        }

        internal void Terminate()
        {
            if (IsExecutionThread)
            {
                TerminateOnExecutionThread();
                return;
            }

            List<Exception> failures = null;
            WorkItemCompletion completion = null;
            bool ownsTermination = false;

            try
            {
                lock (_admissionLock)
                {
                    if (_state == ExecutionThreadState.Running)
                    {
                        _state = ExecutionThreadState.Closing;
                        completion = new WorkItemCompletion();
                        try
                        {
                            _workItems.Add(
                                new ExecutionThreadWorkItem(
                                    WorkItemType.TerminateAction,
                                    completion));
                            _workItems.CompleteAdding();
                            ownsTermination = true;
                        }
                        catch
                        {
                            completion.Dispose();
                            completion = null;
                            if (!_workItems.IsAddingCompleted)
                            {
                                _workItems.CompleteAdding();
                            }
                            throw;
                        }
                    }
                }

                if (ownsTermination)
                {
                    completion.WaitAndThrow();
                }
                else
                {
                    WaitForTermination();
                    Exception terminationException;
                    lock (_admissionLock)
                    {
                        terminationException = _terminationException;
                    }
                    if (terminationException != null)
                    {
                        ExceptionDispatchInfo.Capture(terminationException).Throw();
                    }
                }
            }
            catch (Exception exception)
            {
                AddFailure(exception, ref failures);
            }
            finally
            {
                JoinTerminatedThread();
            }

            // The worker is joined, so no active or later boundary can own any
            // residual terminal error left by catastrophic queue rejection.
            AddFailure(TakePendingException(), ref failures);
            ThrowFailures(
                "OpenGL execution-thread termination encountered multiple failures.",
                failures);
        }

        internal void JoinTerminatedThread()
        {
            Debug.Assert(!IsExecutionThread);
            WaitForTermination();
            _thread.Join();
        }

        private static void AddFailure(Exception exception, ref List<Exception> failures)
        {
            if (exception == null)
            {
                return;
            }

            failures ??= new List<Exception>();
            failures.Add(exception);
        }

        private static void ThrowFailures(string aggregateMessage, List<Exception> failures)
        {
            Exception failure = CreateFailure(aggregateMessage, failures);
            if (failure == null)
            {
                return;
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static Exception CreateFailure(
            string aggregateMessage,
            List<Exception> failures)
        {
            if (failures == null || failures.Count == 0)
            {
                return null;
            }

            return failures.Count == 1
                ? failures[0]
                : new AggregateException(aggregateMessage, failures);
        }

        internal void WaitForIdle()
        {
            ExecuteBoundaryWork(
                new ExecutionThreadWorkItem(
                    new WorkItemCompletion(),
                    isFullFlush: true),
                "OpenGL idle synchronization encountered multiple failures.");
        }

        internal void SetSyncToVerticalBlank(bool value)
        {
            DispatchAsynchronous(new ExecutionThreadWorkItem(value), out _);
        }

        internal void SwapBuffers()
        {
            ThrowIfExecutingGpuWork("present a swapchain");
            ExecuteBoundaryWork(
                new ExecutionThreadWorkItem(
                    WorkItemType.SwapBuffers,
                    new WorkItemCompletion()),
                "OpenGL presentation encountered multiple failures.");
        }

        internal void FlushAndFinish()
        {
            ThrowIfExecutingGpuWork("flush and finish the device");
            ExecuteBoundaryWork(
                new ExecutionThreadWorkItem(
                    new WorkItemCompletion(),
                    isFullFlush: true),
                "OpenGL flush-and-finish encountered multiple failures.");
        }

        internal void InitializeResource(OpenGLDeferredResource deferredResource)
        {
            ExecuteSynchronously(
                new ExecutionThreadWorkItem(
                    deferredResource,
                    new WorkItemCompletion()));
        }

        private void ExecuteSynchronously(ExecutionThreadWorkItem workItem)
        {
            Debug.Assert(workItem.Completion != null);
            if (IsExecutionThread)
            {
                ExecuteWorkItem(workItem);
            }
            else
            {
                try
                {
                    QueueWorkItem(workItem);
                }
                catch
                {
                    workItem.Completion.Dispose();
                    throw;
                }
            }

            workItem.Completion.WaitAndThrow();
        }

        private void ExecuteBoundaryWork(
            ExecutionThreadWorkItem workItem,
            string aggregateMessage)
        {
            List<Exception> failures = null;
            bool externalCaller = !IsExecutionThread;
            try
            {
                if (IsExecutionThread)
                {
                    ExecuteSnapshotBoundary(workItem);
                }
                else
                {
                    ExecuteSynchronously(workItem);
                }
            }
            catch (Exception exception)
            {
                AddFailure(exception, ref failures);
            }

            if (externalCaller
                && workItem.Completion.ExecutionThreadTerminatedAtCompletion)
            {
                try
                {
                    JoinTerminatedThread();
                }
                catch (Exception exception)
                {
                    AddFailure(exception, ref failures);
                }
            }

            ThrowFailures(aggregateMessage, failures);
        }

        /// <summary>
        /// Establishes a FIFO boundary while already running on the GL thread.
        /// The marker is admitted atomically at the queue tail, then this thread
        /// pumps all work accepted before it. Completion state, rather than item
        /// identity, is authoritative so a nested snapshot pump can consume an
        /// outer marker without stranding the outer caller.
        /// </summary>
        private void ExecuteSnapshotBoundary(ExecutionThreadWorkItem boundary)
        {
            Debug.Assert(boundary.Completion != null);
            bool boundaryRegistered = _activeSnapshotBoundaries.Add(boundary.Completion);
            Debug.Assert(boundaryRegistered);
            try
            {
                try
                {
                    QueueWorkItem(boundary);
                }
                catch
                {
                    boundary.Completion.Dispose();
                    throw;
                }

                bool transactionBarrierEncountered = false;
                bool submittedWorkBlockedByTransaction = false;
                while (!boundary.Completion.IsCompleted)
                {
                    ExecutionThreadWorkItem accepted;
                    if (!transactionBarrierEncountered
                        && _deferredOrderedWorkItems.Count != 0)
                    {
                        ExecutionThreadWorkItem deferredHead = _deferredOrderedWorkItems.Peek();
                        bool isActiveDeferredBoundary = deferredHead.Completion != null
                            && _activeSnapshotBoundaries.Contains(deferredHead.Completion);
                        if (isActiveDeferredBoundary
                            || deferredHead.IsSnapshotPumpableNativeWork)
                        {
                            bool dequeued = TryTakeDeferredOrderedWorkItem(out accepted);
                            Debug.Assert(dequeued);
                        }
                        else
                        {
                            // The active outer transaction cannot reenter this
                            // earlier admitted API transaction. Leave the FIFO
                            // prefix intact and inspect it for native work which
                            // makes this boundary impossible to satisfy.
                            transactionBarrierEncountered = true;
                            foreach (ExecutionThreadWorkItem deferred
                                in _deferredOrderedWorkItems)
                            {
                                if (deferred.IsSnapshotPumpableNativeWork)
                                {
                                    submittedWorkBlockedByTransaction = true;
                                    break;
                                }
                            }
                            continue;
                        }
                    }
                    else
                    {
                        try
                        {
                            accepted = _workItems.Take();
                        }
                        catch (InvalidOperationException exception) when (_workItems.IsCompleted)
                        {
                            boundary.Completion.Complete(
                                new NeoVeldridException(
                                    "The OpenGL execution queue closed before reaching its snapshot boundary.",
                                    exception));
                            break;
                        }
                    }

                    bool isCurrentBoundary = ReferenceEquals(
                        accepted.Completion,
                        boundary.Completion);
                    bool isActiveBoundary = accepted.Completion != null
                        && _activeSnapshotBoundaries.Contains(accepted.Completion);
                    if (isCurrentBoundary && submittedWorkBlockedByTransaction)
                    {
                        List<Exception> failures = null;
                        AddFailure(TakePendingException(), ref failures);
                        AddFailure(
                            new InvalidOperationException(
                                "The OpenGL execution thread cannot establish a nested boundary "
                                + "because a previously admitted API transaction separates it "
                                + "from submitted native work. Complete the outer execution-thread "
                                + "action before waiting for that work."),
                            ref failures);
                        boundary.Completion.Complete(
                            CreateFailure(
                                "OpenGL nested-boundary admission encountered multiple failures.",
                                failures));
                        continue;
                    }

                    if (!isActiveBoundary
                        && (!accepted.IsSnapshotPumpableNativeWork
                            || transactionBarrierEncountered))
                    {
                        // Preserve strict FIFO when a transaction separates this
                        // snapshot from later native work. The current boundary
                        // will fail deterministically instead of either reentering
                        // that transaction or overtaking it and returning a false
                        // idle/completion result.
                        if (!accepted.IsSnapshotPumpableNativeWork)
                        {
                            transactionBarrierEncountered = true;
                        }
                        else
                        {
                            submittedWorkBlockedByTransaction = true;
                        }
                        DeferOrderedWorkItem(accepted);
                        continue;
                    }

                    ExecuteWorkItem(accepted);
                }

                boundary.Completion.WaitAndThrow();
            }
            finally
            {
                if (boundaryRegistered)
                {
                    _activeSnapshotBoundaries.Remove(boundary.Completion);
                }
            }
        }

        private void DispatchAsynchronous(
            ExecutionThreadWorkItem workItem,
            out bool ownershipTransferred)
        {
            ownershipTransferred = false;
            if (IsExecutionThread)
            {
                EnsureAdmissionOpen();
                ownershipTransferred = true;
                ExecuteWorkItem(workItem);
                CheckExceptions();
            }
            else
            {
                QueueWorkItem(workItem);
                ownershipTransferred = true;
            }
        }

        private void EnsureAdmissionOpen()
        {
            lock (_admissionLock)
            {
                if (_state != ExecutionThreadState.Running)
                {
                    throw new ObjectDisposedException(
                        nameof(OpenGLGraphicsDevice),
                        "The OpenGL execution thread is no longer accepting work.");
                }
            }
        }

        private void TerminateOnExecutionThread()
        {
            List<Exception> failures = null;
            AddFailure(TakePendingException(), ref failures);

            lock (_admissionLock)
            {
                if (_state == ExecutionThreadState.Running)
                {
                    _state = ExecutionThreadState.Closing;
                    _workItems.CompleteAdding();
                }
            }

            ObjectDisposedException rejectedByTeardown = new ObjectDisposedException(
                nameof(OpenGLGraphicsDevice),
                "The OpenGL graphics device began teardown before this accepted operation executed.");
            while (!_terminated && TryTakeAcceptedWork(out ExecutionThreadWorkItem acceptedWork))
            {
                if (acceptedWork.Type == WorkItemType.DisposalAction)
                {
                    // A previously admitted external Dispose call must complete
                    // its proxy so that caller can join this owning transaction.
                    ExecuteWorkItem(acceptedWork);
                }
                else
                {
                    RejectAcceptedWork(acceptedWork, rejectedByTeardown);
                }
            }

            if (!_terminated)
            {
                try
                {
                    ExecuteTermination();
                }
                catch (Exception exception)
                {
                    AddFailure(exception, ref failures);
                }
            }
            else if (_terminationException != null)
            {
                AddFailure(_terminationException, ref failures);
            }

            AddFailure(TakePendingException(), ref failures);
            ThrowFailures(
                "OpenGL execution-thread termination encountered multiple failures.",
                failures);
        }

        private void ExecuteTermination()
        {
            Exception terminationFailure = null;
            try
            {
                List<Exception> failures = null;
                bool contextCurrent = AttemptPlatformCleanup(
                    () =>
                    {
                        _makeCurrent(_gd._glContext);
                        IntPtr currentContext = _getCurrentContext();
                        if (currentContext != _gd._glContext)
                        {
                            throw new NeoVeldridException(
                                "The platform did not make the owned OpenGL context current during teardown.");
                        }
                    },
                    ignoreContextUnavailable: true,
                    ref failures);
                if (contextCurrent)
                {
                    // External teardown reaches this point after a queue boundary. Reentrant
                    // teardown originates inside ExecuteOnGLThread and drains accepted work in
                    // TerminateOnExecutionThread, so it needs its GPU completion boundary here.
                    AttemptPlatformCleanup(
                        () =>
                        {
                            _gd.GL.Flush();
                            _gd.GL.Finish();
                        },
                        ignoreContextUnavailable: true,
                        ref failures);
                    AttemptPlatformCleanup(
                        _gd.FlushDisposables,
                        ignoreContextUnavailable: true,
                        ref failures);
                    AttemptPlatformCleanup(
                        () => _gd.ClearOwnedContext("context destruction"),
                        ignoreContextUnavailable: false,
                        ref failures);
                }

                if (_gd._glContext != IntPtr.Zero)
                {
                    AttemptPlatformCleanup(
                        () =>
                        {
                            _gd._deleteContext(_gd._glContext);
                            _gd.ConfirmContextDestroyed();
                        },
                        ignoreContextUnavailable: false,
                        ref failures);
                }

                ThrowPlatformCleanupFailures(failures);
            }
            catch (Exception exception)
            {
                terminationFailure = exception;
                throw;
            }
            finally
            {
                lock (_admissionLock)
                {
                    _terminationException = terminationFailure;
                    _terminated = true;
                    _state = ExecutionThreadState.Closed;
                    Monitor.PulseAll(_admissionLock);
                }
            }
        }

        private void FailExecutionThread(Exception failure)
        {
            lock (_admissionLock)
            {
                _terminationException = failure;
                _state = ExecutionThreadState.Closed;
                if (!_workItems.IsAddingCompleted)
                {
                    _workItems.CompleteAdding();
                }
            }

            while (TryTakeAcceptedWork(out ExecutionThreadWorkItem rejectedWork))
            {
                RejectAcceptedWork(rejectedWork, failure);
            }

            AddPendingException(failure);
            lock (_admissionLock)
            {
                _terminated = true;
                Monitor.PulseAll(_admissionLock);
            }
        }

        private void WaitForTermination()
        {
            lock (_admissionLock)
            {
                while (!_terminated)
                {
                    Monitor.Wait(_admissionLock);
                }
            }
        }

        private void RejectAcceptedWork(
            ExecutionThreadWorkItem workItem,
            Exception executionThreadFailure)
        {
            List<Exception> failures = null;
            AddFailure(executionThreadFailure, ref failures);
            try
            {
                switch (workItem.Type)
                {
                    case WorkItemType.ExecuteList:
                    {
                        OpenGLCommandEntryList list = (OpenGLCommandEntryList)workItem.Object0;
                        if (!_gd.CheckCommandListDisposal(list.Parent))
                        {
                            list.Parent.OnCompleted(list);
                        }
                        ((OpenGLFence)workItem.Object1)?.CompleteSubmission(
                            executionThreadFailure);
                        break;
                    }
                    case WorkItemType.UpdateBuffer:
                        _gd.StagingMemoryPool.Free(
                            _gd.StagingMemoryPool.RetrieveById(workItem.UInt1));
                        break;
                    case WorkItemType.UpdateTexture:
                        _gd.StagingMemoryPool.Free(
                            _gd.StagingMemoryPool.RetrieveById(workItem.UInt0));
                        _gd.StagingMemoryPool.Free(
                            _gd.StagingMemoryPool.RetrieveById(workItem.UInt1));
                        break;
                }
            }
            catch (Exception ownershipFailure)
            {
                AddFailure(ownershipFailure, ref failures);
            }

            Exception rejectionFailure = failures.Count == 1
                ? failures[0]
                : new AggregateException(
                    "OpenGL execution-thread failure also prevented accepted-work rollback.",
                    failures);
            if (workItem.Completion != null)
            {
                workItem.Completion.Complete(rejectionFailure);
            }
            else
            {
                AddPendingException(rejectionFailure);
            }
        }
    }

    public enum WorkItemType : byte
    {
        Map,
        Unmap,
        ExecuteList,
        UpdateBuffer,
        UpdateTexture,
        GenericAction,
        DisposalAction,
        TerminateAction,
        SetSyncToVerticalBlank,
        SwapBuffers,
        WaitForIdle,
        InitializeResource,
    }

    private unsafe struct ExecutionThreadWorkItem
    {
        public readonly WorkItemType Type;
        public readonly object Object0;
        public readonly object Object1;
        public readonly uint UInt0;
        public readonly uint UInt1;
        public readonly uint UInt2;
        public readonly WorkItemCompletion Completion;

        // Snapshot waits may advance only bounded native work with no unguarded
        // user callback. API transactions and arbitrary platform callbacks are
        // deferred unless their completion identifies an active snapshot marker.
        public readonly bool IsSnapshotPumpableNativeWork =>
            Type == WorkItemType.Map
            || Type == WorkItemType.Unmap
            || Type == WorkItemType.ExecuteList
            || Type == WorkItemType.UpdateBuffer
            || Type == WorkItemType.UpdateTexture
            || Type == WorkItemType.WaitForIdle
            || Type == WorkItemType.InitializeResource;

        public readonly bool IsGpuWork =>
            Type == WorkItemType.ExecuteList
            || Type == WorkItemType.UpdateBuffer
            || Type == WorkItemType.UpdateTexture;

        public ExecutionThreadWorkItem(
            MappableResource resource,
            MapParams* mapResult,
            WorkItemCompletion completion)
        {
            Type = WorkItemType.Map;
            Object0 = resource;
            Object1 = null;

            Util.PackIntPtr((IntPtr)mapResult, out UInt0, out UInt1);
            UInt2 = 0;
            Completion = completion;
        }

        public ExecutionThreadWorkItem(
            OpenGLCommandEntryList commandList,
            OpenGLFence fence,
            WorkItemCompletion completion)
        {
            Type = WorkItemType.ExecuteList;
            Object0 = commandList;
            Object1 = fence;

            UInt0 = 0;
            UInt1 = 0;
            UInt2 = 0;
            Completion = completion;
        }

        public ExecutionThreadWorkItem(DeviceBuffer updateBuffer, uint offsetInBytes, StagingBlock stagedSource)
        {
            Type = WorkItemType.UpdateBuffer;
            Object0 = updateBuffer;
            Object1 = null;

            UInt0 = offsetInBytes;
            UInt1 = stagedSource.Id;
            UInt2 = 0;
            Completion = null;
        }

        public ExecutionThreadWorkItem(Action a, WorkItemCompletion completion)
            : this(WorkItemType.GenericAction, a, completion)
        {
        }

        private ExecutionThreadWorkItem(
            WorkItemType type,
            Action action,
            WorkItemCompletion completion)
        {
            Type = type;
            Object0 = action;
            Object1 = null;

            UInt0 = 0;
            UInt1 = 0;
            UInt2 = 0;
            Completion = completion;
        }

        public static ExecutionThreadWorkItem CreateDisposal(
            Action disposeCore,
            WorkItemCompletion completion) =>
            new ExecutionThreadWorkItem(
                WorkItemType.DisposalAction,
                disposeCore,
                completion);

        public ExecutionThreadWorkItem(Texture texture, uint argBlockId, uint dataBlockId)
        {
            Type = WorkItemType.UpdateTexture;
            Object0 = texture;
            Object1 = null;

            UInt0 = argBlockId;
            UInt1 = dataBlockId;
            UInt2 = 0;
            Completion = null;
        }

        public ExecutionThreadWorkItem(WorkItemCompletion completion, bool isFullFlush)
        {
            Type = WorkItemType.WaitForIdle;
            Object0 = null;
            Object1 = null;

            UInt0 = isFullFlush ? 1u : 0u;
            UInt1 = 0;
            UInt2 = 0;
            Completion = completion;
        }

        public ExecutionThreadWorkItem(bool value)
        {
            Type = WorkItemType.SetSyncToVerticalBlank;
            Object0 = null;
            Object1 = null;

            UInt0 = value ? 1u : 0u;
            UInt1 = 0;
            UInt2 = 0;
            Completion = null;
        }

        public ExecutionThreadWorkItem(
            WorkItemType type,
            WorkItemCompletion completion)
        {
            Type = type;
            Object0 = null;
            Object1 = null;

            UInt0 = 0;
            UInt1 = 0;
            UInt2 = 0;
            Completion = completion;
        }

        public ExecutionThreadWorkItem(
            OpenGLDeferredResource deferredResource,
            WorkItemCompletion completion)
        {
            Type = WorkItemType.InitializeResource;
            Object0 = deferredResource;
            Object1 = null;

            UInt0 = 0;
            UInt1 = 0;
            UInt2 = 0;
            Completion = completion;
        }
    }

    private enum ExecutionThreadState : byte
    {
        Running,
        Closing,
        Closed,
    }

    private sealed class WorkItemCompletion : IDisposable
    {
        private readonly ManualResetEventSlim _completedEvent = new ManualResetEventSlim(false);
        private Exception _exception;
        private int _executionThreadTerminatedAtCompletion;
        private int _completed;
        private int _disposed;

        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public bool ExecutionThreadTerminatedAtCompletion =>
            Volatile.Read(ref _executionThreadTerminatedAtCompletion) != 0;

        public void Complete(Exception exception, bool executionThreadTerminated = false)
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) == 0)
            {
                _exception = exception;
                if (executionThreadTerminated)
                {
                    Volatile.Write(ref _executionThreadTerminatedAtCompletion, 1);
                }
                _completedEvent.Set();
            }
        }

        public void WaitAndThrow()
        {
            _completedEvent.Wait();
            Exception exception = _exception;
            Dispose();
            if (exception != null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _completedEvent.Dispose();
            }
        }
    }

    private struct MapParams
    {
        public MapMode MapMode;
        public uint Subresource;
        public bool Map;
        public bool Succeeded;
        public IntPtr Data;
        public uint DataSize;
        public uint RowPitch;
        public uint DepthPitch;
    }

    internal struct MappedResourceInfoWithStaging
    {
        public int RefCount;
        public MapMode Mode;
        public MappedResource MappedResource;
        public StagingBlock StagingBlock;
    }

}
