using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Core.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace NeoVeldrid.D3D11;

internal unsafe class D3D11Swapchain : Swapchain
{
    private readonly D3D11GraphicsDevice _gd;
    private readonly PixelFormat? _depthFormat;
    private ComPtr<IDXGISwapChain> _dxgiSwapChain;
    private bool _vsync;
    private int _syncInterval;
    private D3D11Framebuffer _framebuffer;
    private D3D11Texture _depthTexture;
    private D3D11Texture _backBufferVdTexture;
    private uint _nativeBufferWidth;
    private uint _nativeBufferHeight;
    private float _pixelScale = 1f;
    private bool _disposed;
    private string _name;

    private readonly object _referencedCLsLock = new object();
    private readonly HashSet<D3D11CommandList> _referencedCLs = new HashSet<D3D11CommandList>();

    public override Framebuffer Framebuffer => _framebuffer;

    public override string Name
    {
        get => _name;
        set => _name = value;
    }

    public override bool SyncToVerticalBlank
    {
        get => _vsync;
        set
        {
            _vsync = value;
            _syncInterval = D3D11Util.GetSyncInterval(value);
        }
    }

    private readonly Format _colorFormat;

    /// <summary>Returns a raw pointer to the underlying DXGI swap chain.</summary>
    public IDXGISwapChain* DxgiSwapChain => _dxgiSwapChain;

    public int SyncInterval => _syncInterval;

    public D3D11Swapchain(D3D11GraphicsDevice gd, ref SwapchainDescription description)
    {
        _gd = gd;
        _depthFormat = description.DepthFormat;
        SyncToVerticalBlank = description.SyncToVerticalBlank;

        _colorFormat = description.ColorSrgb
            ? Format.FormatB8G8R8A8UnormSrgb
            : Format.FormatB8G8R8A8Unorm;

        try
        {
            if (description.Source is not Win32SwapchainSource win32Source)
            {
                throw new NeoVeldridException(
                    $"Unsupported swapchain source type: {description.Source?.GetType().Name}");
            }

            CreateNativeSwapchain(win32Source, description.Width, description.Height);
            Resize(description.Width, description.Height);
        }
        catch (Exception creationError)
        {
            List<Exception> cleanupFailures = null;
            AttemptCleanup(Dispose, ref cleanupFailures);
            ThrowAcquisitionFailure(
                "Direct3D 11 swapchain creation and cleanup both failed.",
                creationError,
                cleanupFailures);
        }
    }

    private void CreateNativeSwapchain(Win32SwapchainSource source, uint width, uint height)
    {
        SwapChainDesc description = new SwapChainDesc
        {
            BufferCount = 2,
            Windowed = 1,
            BufferDesc = new ModeDesc
            {
                Width = width,
                Height = height,
                Format = _colorFormat,
            },
            OutputWindow = source.Hwnd,
            SampleDesc = new SampleDesc(1, 0),
            SwapEffect = SwapEffect.Discard,
            // DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x00000020
            BufferUsage = 0x00000020u,
        };

        IDXGIFactory* factory = null;
        IDXGISwapChain* swapchain = null;
        try
        {
            Guid factoryGuid = IDXGIFactory.Guid;
            SilkMarshal.ThrowHResult(
                _gd.Adapter->GetParent(&factoryGuid, (void**)&factory));

            SilkMarshal.ThrowHResult(
                factory->CreateSwapChain(
                    (IUnknown*)_gd.Device,
                    &description,
                    &swapchain));

            // Transfer the CreateSwapChain reference before any later operation
            // can fail, so constructor cleanup can always see and release it.
            _dxgiSwapChain = default;
            _dxgiSwapChain.Handle = swapchain;
            swapchain = null;
            _nativeBufferWidth = width;
            _nativeBufferHeight = height;

            // DXGI_MWA_NO_ALT_ENTER = 0x2. Nonnegative DXGI status results are
            // successful; only failed HRESULTs abort construction.
            SilkMarshal.ThrowHResult(
                factory->MakeWindowAssociation(source.Hwnd, 0x2u));
        }
        finally
        {
            if (swapchain != null)
            {
                swapchain->Release();
            }
            if (factory != null)
            {
                factory->Release();
            }
        }
    }

    public override void Resize(uint width, uint height)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(D3D11Swapchain));
        }

        lock (_referencedCLsLock)
        {
            foreach (D3D11CommandList commandList in _referencedCLs)
            {
                commandList.Reset();
            }

            _referencedCLs.Clear();
        }

        bool hasFramebufferResources =
            _framebuffer != null
            || _depthTexture != null
            || _backBufferVdTexture != null;
        if (hasFramebufferResources)
        {
            List<Exception> releaseFailures = null;
            ReleaseFramebufferResources(ref releaseFailures);
            ThrowCleanupFailures(
                "Direct3D 11 swapchain resize could not release the previous framebuffer resources.",
                releaseFailures);
        }

        uint actualWidth = (uint)(width * _pixelScale);
        uint actualHeight = (uint)(height * _pixelScale);
        bool resizeBuffers =
            actualWidth != _nativeBufferWidth
            || actualHeight != _nativeBufferHeight;
        if (resizeBuffers)
        {
            SilkMarshal.ThrowHResult(
                _dxgiSwapChain.Handle->ResizeBuffers(
                    2,
                    actualWidth,
                    actualHeight,
                    _colorFormat,
                    0u));
            _nativeBufferWidth = actualWidth;
            _nativeBufferHeight = actualHeight;
        }

        CreateFramebufferResources(actualWidth, actualHeight);
    }

    private void CreateFramebufferResources(uint width, uint height)
    {
        ID3D11Texture2D* backBuffer = null;
        D3D11Texture depthTexture = null;
        D3D11Texture backBufferTexture = null;
        D3D11Framebuffer framebuffer = null;

        try
        {
            try
            {
                Guid texture2DGuid = ID3D11Texture2D.Guid;
                SilkMarshal.ThrowHResult(
                    _dxgiSwapChain.Handle->GetBuffer(
                        0,
                        &texture2DGuid,
                        (void**)&backBuffer));

                if (_depthFormat != null)
                {
                    TextureDescription depthDescription = new TextureDescription(
                        width,
                        height,
                        1,
                        1,
                        1,
                        _depthFormat.Value,
                        TextureUsage.DepthStencil,
                        TextureType.Texture2D);
                    depthTexture = new D3D11Texture(_gd.Device, ref depthDescription);
                }

                backBufferTexture = new D3D11Texture(
                    backBuffer,
                    TextureType.Texture2D,
                    D3D11Formats.ToVdFormat(_colorFormat));

                FramebufferDescription framebufferDescription =
                    new FramebufferDescription(depthTexture, backBufferTexture);
                framebuffer = new D3D11Framebuffer(
                    _gd.Device,
                    ref framebufferDescription)
                {
                    Swapchain = this,
                };

                // Publish the complete framebuffer set atomically with respect
                // to exceptions. Locals own every resource until this point.
                _depthTexture = depthTexture;
                depthTexture = null;
                _backBufferVdTexture = backBufferTexture;
                backBufferTexture = null;
                _framebuffer = framebuffer;
                framebuffer = null;
            }
            finally
            {
                // D3D11Texture takes its own AddRef when wrapping a back buffer.
                // This releases only the raw GetBuffer reference.
                if (backBuffer != null)
                {
                    backBuffer->Release();
                }
            }
        }
        catch (Exception acquisitionError)
        {
            List<Exception> cleanupFailures = null;
            if (framebuffer != null)
            {
                AttemptCleanup(framebuffer.Dispose, ref cleanupFailures);
            }
            if (backBufferTexture != null)
            {
                AttemptCleanup(backBufferTexture.Dispose, ref cleanupFailures);
            }
            if (depthTexture != null)
            {
                AttemptCleanup(depthTexture.Dispose, ref cleanupFailures);
            }

            ThrowAcquisitionFailure(
                "Direct3D 11 framebuffer acquisition and cleanup both failed.",
                acquisitionError,
                cleanupFailures);
        }
    }

    public void AddCommandListReference(D3D11CommandList commandList)
    {
        lock (_referencedCLsLock)
        {
            _referencedCLs.Add(commandList);
        }
    }

    public void RemoveCommandListReference(D3D11CommandList commandList)
    {
        lock (_referencedCLsLock)
        {
            _referencedCLs.Remove(commandList);
        }
    }

    public override bool IsDisposed => _disposed;

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        List<Exception> failures = null;
        ReleaseFramebufferResources(ref failures);
        if (_dxgiSwapChain.Handle != null)
        {
            AttemptCleanup(_dxgiSwapChain.Dispose, ref failures);
        }
        _dxgiSwapChain = default;

        lock (_referencedCLsLock)
        {
            _referencedCLs.Clear();
        }

        ThrowCleanupFailures(
            "Direct3D 11 swapchain teardown encountered multiple failures.",
            failures);
    }

    private void ReleaseFramebufferResources(ref List<Exception> failures)
    {
        D3D11Framebuffer framebuffer = _framebuffer;
        D3D11Texture depthTexture = _depthTexture;
        D3D11Texture backBufferTexture = _backBufferVdTexture;
        _framebuffer = null;
        _depthTexture = null;
        _backBufferVdTexture = null;

        if (framebuffer != null)
        {
            AttemptCleanup(framebuffer.Dispose, ref failures);
        }
        if (depthTexture != null)
        {
            AttemptCleanup(depthTexture.Dispose, ref failures);
        }
        if (backBufferTexture != null)
        {
            AttemptCleanup(backBufferTexture.Dispose, ref failures);
        }
    }

    private static void AttemptCleanup(Action cleanup, ref List<Exception> failures)
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

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowAcquisitionFailure(
        string message,
        Exception acquisitionError,
        List<Exception> cleanupFailures)
    {
        if (cleanupFailures != null && cleanupFailures.Count != 0)
        {
            List<Exception> failures = new List<Exception>(cleanupFailures.Count + 1)
            {
                acquisitionError,
            };
            failures.AddRange(cleanupFailures);
            throw new AggregateException(message, failures);
        }

        ExceptionDispatchInfo.Capture(acquisitionError).Throw();
        throw new UnreachableException();
    }

    private static void ThrowCleanupFailures(string message, List<Exception> failures)
    {
        if (failures == null || failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(message, failures);
    }
}
