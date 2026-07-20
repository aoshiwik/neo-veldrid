using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace NeoVeldrid.D3D11;

internal unsafe class D3D11Framebuffer : Framebuffer
{
    private string _name;
    private bool _disposed;
    private ComPtr<ID3D11DepthStencilView> _depthStencilView;

    public ComPtr<ID3D11RenderTargetView>[] RenderTargetViews { get; }
    public ID3D11DepthStencilView* DepthStencilView => _depthStencilView;

    // Only non-null if this is the Framebuffer for a Swapchain.
    internal D3D11Swapchain Swapchain { get; set; }

    public override bool IsDisposed => _disposed;

    public D3D11Framebuffer(ID3D11Device* device, ref FramebufferDescription description)
        : base(description.DepthTarget, description.ColorTargets)
    {
        ComPtr<ID3D11DepthStencilView> depthStencilView = default;
        ComPtr<ID3D11RenderTargetView>[] renderTargetViews =
            description.ColorTargets != null && description.ColorTargets.Length > 0
                ? new ComPtr<ID3D11RenderTargetView>[description.ColorTargets.Length]
                : Array.Empty<ComPtr<ID3D11RenderTargetView>>();

        try
        {
            if (description.DepthTarget != null)
            {
                D3D11Texture d3dDepthTarget = Util.AssertSubtype<Texture, D3D11Texture>(
                    description.DepthTarget.Value.Target);
                DepthStencilViewDesc dsvDesc = new DepthStencilViewDesc
                {
                    Format = D3D11Formats.GetDepthFormat(d3dDepthTarget.Format),
                };
                if (d3dDepthTarget.ArrayLayers == 1)
                {
                    if (d3dDepthTarget.SampleCount == TextureSampleCount.Count1)
                    {
                        dsvDesc.ViewDimension = DsvDimension.Texture2D;
                        dsvDesc.Texture2D.MipSlice =
                            (uint)description.DepthTarget.Value.MipLevel;
                    }
                    else
                    {
                        dsvDesc.ViewDimension = DsvDimension.Texture2Dms;
                    }
                }
                else
                {
                    if (d3dDepthTarget.SampleCount == TextureSampleCount.Count1)
                    {
                        dsvDesc.ViewDimension = DsvDimension.Texture2Darray;
                        dsvDesc.Texture2DArray.FirstArraySlice =
                            (uint)description.DepthTarget.Value.ArrayLayer;
                        dsvDesc.Texture2DArray.ArraySize = 1;
                        dsvDesc.Texture2DArray.MipSlice =
                            (uint)description.DepthTarget.Value.MipLevel;
                    }
                    else
                    {
                        dsvDesc.ViewDimension = DsvDimension.Texture2Dmsarray;
                        dsvDesc.Texture2DMSArray.FirstArraySlice =
                            (uint)description.DepthTarget.Value.ArrayLayer;
                        dsvDesc.Texture2DMSArray.ArraySize = 1;
                    }
                }

                ID3D11DepthStencilView* rawDepthStencilView = null;
                try
                {
                    int creationResult = device->CreateDepthStencilView(
                        d3dDepthTarget.DeviceTexture,
                        in dsvDesc,
                        &rawDepthStencilView);
                    SilkMarshal.ThrowHResult(creationResult);
                    depthStencilView.Handle = rawDepthStencilView;
                    rawDepthStencilView = null;
                }
                finally
                {
                    if (rawDepthStencilView != null)
                    {
                        rawDepthStencilView->Release();
                    }
                }
            }

            for (int i = 0; i < renderTargetViews.Length; i++)
            {
                D3D11Texture d3dColorTarget = Util.AssertSubtype<Texture, D3D11Texture>(
                    description.ColorTargets[i].Target);
                RenderTargetViewDesc rtvDesc = new RenderTargetViewDesc
                {
                    Format = D3D11Formats.ToDxgiFormat(d3dColorTarget.Format, false),
                };
                if (d3dColorTarget.ArrayLayers > 1 || (d3dColorTarget.Usage & TextureUsage.Cubemap) != 0)
                {
                    if (d3dColorTarget.SampleCount == TextureSampleCount.Count1)
                    {
                        rtvDesc.ViewDimension = RtvDimension.Texture2Darray;
                        rtvDesc.Texture2DArray.ArraySize = 1;
                        rtvDesc.Texture2DArray.FirstArraySlice =
                            (uint)description.ColorTargets[i].ArrayLayer;
                        rtvDesc.Texture2DArray.MipSlice =
                            (uint)description.ColorTargets[i].MipLevel;
                    }
                    else
                    {
                        rtvDesc.ViewDimension = RtvDimension.Texture2Dmsarray;
                        rtvDesc.Texture2DMSArray.ArraySize = 1;
                        rtvDesc.Texture2DMSArray.FirstArraySlice = (uint)description.ColorTargets[i].ArrayLayer;
                    }
                }
                else
                {
                    if (d3dColorTarget.SampleCount == TextureSampleCount.Count1)
                    {
                        rtvDesc.ViewDimension = RtvDimension.Texture2D;
                        rtvDesc.Texture2D.MipSlice =
                            (uint)description.ColorTargets[i].MipLevel;
                    }
                    else
                    {
                        rtvDesc.ViewDimension = RtvDimension.Texture2Dms;
                    }
                }

                ID3D11RenderTargetView* rawRenderTargetView = null;
                try
                {
                    int creationResult = device->CreateRenderTargetView(
                        d3dColorTarget.DeviceTexture,
                        in rtvDesc,
                        &rawRenderTargetView);
                    SilkMarshal.ThrowHResult(creationResult);
                    renderTargetViews[i].Handle = rawRenderTargetView;
                    rawRenderTargetView = null;
                }
                finally
                {
                    if (rawRenderTargetView != null)
                    {
                        rawRenderTargetView->Release();
                    }
                }
            }

            _depthStencilView = depthStencilView;
            depthStencilView = default;
            RenderTargetViews = renderTargetViews;
            renderTargetViews = null;
        }
        catch (Exception acquisitionError)
        {
            List<Exception> cleanupFailures = null;
            if (depthStencilView.Handle != null)
            {
                AttemptCleanup(depthStencilView.Dispose, ref cleanupFailures);
            }
            if (renderTargetViews != null)
            {
                foreach (ComPtr<ID3D11RenderTargetView> renderTargetView in renderTargetViews)
                {
                    if (renderTargetView.Handle != null)
                    {
                        AttemptCleanup(renderTargetView.Dispose, ref cleanupFailures);
                    }
                }
            }

            ThrowAcquisitionFailure(acquisitionError, cleanupFailures);
        }
    }

    public override string Name
    {
        get => _name;
        set
        {
            _name = value;
            for (int i = 0; i < RenderTargetViews.Length; i++)
                D3D11Util.SetDebugName((ID3D11DeviceChild*)RenderTargetViews[i].Handle, value + "_RTV" + i);
            if (_depthStencilView.Handle != null)
                D3D11Util.SetDebugName((ID3D11DeviceChild*)_depthStencilView.Handle, value + "_DSV");
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        List<Exception> failures = null;
        if (_depthStencilView.Handle != null)
        {
            AttemptCleanup(_depthStencilView.Dispose, ref failures);
        }
        _depthStencilView = default;
        foreach (ComPtr<ID3D11RenderTargetView> renderTargetView in RenderTargetViews)
        {
            if (renderTargetView.Handle != null)
            {
                AttemptCleanup(renderTargetView.Dispose, ref failures);
            }
        }

        ThrowCleanupFailures(failures);
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
            throw new AggregateException(
                "Direct3D 11 framebuffer creation and cleanup both failed.",
                failures);
        }

        ExceptionDispatchInfo.Capture(acquisitionError).Throw();
        throw new UnreachableException();
    }

    private static void ThrowCleanupFailures(List<Exception> failures)
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
            "Direct3D 11 framebuffer teardown encountered multiple failures.",
            failures);
    }
}
