using System;
using System.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace NeoVeldrid.D3D11;

internal unsafe class D3D11Texture : Texture
{
    private readonly ID3D11Device* _device;
    private ComPtr<ID3D11Resource> _deviceTexture;
    private string _name;
    private bool _disposed;

    public override uint Width { get; }
    public override uint Height { get; }
    public override uint Depth { get; }
    public override uint MipLevels { get; }
    public override uint ArrayLayers { get; }
    public override PixelFormat Format { get; }
    public override TextureUsage Usage { get; }
    public override TextureType Type { get; }
    public override TextureSampleCount SampleCount { get; }
    public override bool IsDisposed => _disposed;

    public ID3D11Resource* DeviceTexture => _deviceTexture;
    public Format DxgiFormat { get; }
    public Format TypelessDxgiFormat { get; }

    public D3D11Texture(ID3D11Device* device, ref TextureDescription description)
    {
        _device = device;
        Width = description.Width;
        Height = description.Height;
        Depth = description.Depth;
        MipLevels = description.MipLevels;
        ArrayLayers = description.ArrayLayers;
        Format = description.Format;
        Usage = description.Usage;
        Type = description.Type;
        SampleCount = description.SampleCount;

        NativeTextureDescription nativeDescription =
            NativeTextureDescription.Create(description);
        DxgiFormat = nativeDescription.ViewFormat;
        TypelessDxgiFormat = nativeDescription.ResourceFormat;

        ID3D11Resource* nativeTexture = null;
        try
        {
            if (Type == TextureType.Texture1D)
            {
                Texture1DDesc desc1D = new Texture1DDesc
                {
                    Width = nativeDescription.Width,
                    MipLevels = description.MipLevels,
                    ArraySize = nativeDescription.ArraySize,
                    Format = TypelessDxgiFormat,
                    BindFlags = (uint)nativeDescription.BindFlags,
                    CPUAccessFlags = (uint)nativeDescription.CpuAccessFlags,
                    Usage = nativeDescription.ResourceUsage,
                    MiscFlags = (uint)nativeDescription.MiscFlags,
                };

                ID3D11Texture1D* texture = null;
                int creationResult = device->CreateTexture1D(in desc1D, null, &texture);
                nativeTexture = (ID3D11Resource*)texture;
                SilkMarshal.ThrowHResult(creationResult);
            }
            else if (Type == TextureType.Texture2D)
            {
                Texture2DDesc desc2D = new Texture2DDesc
                {
                    Width = nativeDescription.Width,
                    Height = nativeDescription.Height,
                    MipLevels = description.MipLevels,
                    ArraySize = nativeDescription.ArraySize,
                    Format = TypelessDxgiFormat,
                    BindFlags = (uint)nativeDescription.BindFlags,
                    CPUAccessFlags = (uint)nativeDescription.CpuAccessFlags,
                    Usage = nativeDescription.ResourceUsage,
                    SampleDesc = new SampleDesc
                    {
                        Count = FormatHelpers.GetSampleCountUInt32(SampleCount),
                        Quality = 0,
                    },
                    MiscFlags = (uint)nativeDescription.MiscFlags,
                };

                ID3D11Texture2D* texture = null;
                int creationResult = device->CreateTexture2D(in desc2D, null, &texture);
                nativeTexture = (ID3D11Resource*)texture;
                SilkMarshal.ThrowHResult(creationResult);
            }
            else
            {
                Debug.Assert(Type == TextureType.Texture3D);
                Texture3DDesc desc3D = new Texture3DDesc
                {
                    Width = nativeDescription.Width,
                    Height = nativeDescription.Height,
                    Depth = description.Depth,
                    MipLevels = description.MipLevels,
                    Format = TypelessDxgiFormat,
                    BindFlags = (uint)nativeDescription.BindFlags,
                    CPUAccessFlags = (uint)nativeDescription.CpuAccessFlags,
                    Usage = nativeDescription.ResourceUsage,
                    MiscFlags = (uint)nativeDescription.MiscFlags,
                };

                ID3D11Texture3D* texture = null;
                int creationResult = device->CreateTexture3D(in desc3D, null, &texture);
                nativeTexture = (ID3D11Resource*)texture;
                SilkMarshal.ThrowHResult(creationResult);
            }

            _deviceTexture = default;
            _deviceTexture.Handle = nativeTexture;
            nativeTexture = null;
        }
        finally
        {
            // Failed COM calls may still populate output pointers. The field
            // owns only a reference explicitly transferred above.
            if (nativeTexture != null)
            {
                nativeTexture->Release();
            }
        }
    }

    public D3D11Texture(
        ID3D11Device* device,
        ID3D11Texture2D* existingTexture,
        ref TextureDescription description)
    {
        NativeTextureDescription expected =
            NativeTextureDescription.Create(description);
        Texture2DDesc desc;
        existingTexture->GetDesc(&desc);
        ValidateNativeTextureDescription(
            device,
            existingTexture,
            in description,
            in expected,
            in desc);

        _device = device;
        Width = description.Width;
        Height = description.Height;
        Depth = description.Depth;
        MipLevels = description.MipLevels;
        ArrayLayers = description.ArrayLayers;
        Format = description.Format;
        SampleCount = description.SampleCount;
        Type = TextureType.Texture2D;
        Usage = description.Usage;
        DxgiFormat = expected.ViewFormat;
        TypelessDxgiFormat = expected.ResourceFormat;

        // Acquire the wrapper's native ownership only after every potentially
        // throwing metadata conversion has completed.
        existingTexture->AddRef();
        _deviceTexture = default;
        _deviceTexture.Handle = (ID3D11Resource*)existingTexture;
    }

    // Swapchain buffers have runtime-owned descriptors which cannot be
    // reconstructed from a public TextureDescription. This constructor is
    // intentionally separate from ResourceFactory's strict native-import path.
    public D3D11Texture(
        ID3D11Texture2D* swapchainTexture,
        TextureType type,
        PixelFormat format)
    {
        Debug.Assert(type == TextureType.Texture2D);
        Texture2DDesc desc;
        swapchainTexture->GetDesc(&desc);

        ID3D11Device* device = null;
        try
        {
            ((ID3D11DeviceChild*)swapchainTexture)->GetDevice(&device);
            _device = device;
        }
        finally
        {
            // GetDevice calls AddRef; this class stores only a borrowed pointer.
            if (device != null)
            {
                device->Release();
            }
        }

        Width = desc.Width;
        Height = desc.Height;
        Depth = 1;
        MipLevels = desc.MipLevels;
        ArrayLayers = desc.ArraySize;
        Format = format;
        SampleCount = FormatHelpers.GetSampleCount(desc.SampleDesc.Count);
        Type = TextureType.Texture2D;
        Usage = D3D11Formats.GetVdUsage(
            (BindFlag)desc.BindFlags,
            (CpuAccessFlag)desc.CPUAccessFlags,
            (ResourceMiscFlag)desc.MiscFlags);
        DxgiFormat = D3D11Formats.ToDxgiFormat(
            format,
            (Usage & TextureUsage.DepthStencil) != 0);
        TypelessDxgiFormat = D3D11Formats.GetTypelessFormat(DxgiFormat);

        swapchainTexture->AddRef();
        _deviceTexture = default;
        _deviceTexture.Handle = (ID3D11Resource*)swapchainTexture;
    }

    private static void ValidateNativeTextureDescription(
        ID3D11Device* expectedDevice,
        ID3D11Texture2D* existingTexture,
        in TextureDescription publicDescription,
        in NativeTextureDescription expected,
        in Texture2DDesc actual)
    {
        ID3D11Device* actualDevice = null;
        try
        {
            ((ID3D11DeviceChild*)existingTexture)->GetDevice(&actualDevice);
            if (actualDevice != expectedDevice)
            {
                throw new ArgumentException(
                    "The native D3D11 texture belongs to a different graphics device.",
                    "nativeTexture");
            }
        }
        finally
        {
            if (actualDevice != null)
            {
                actualDevice->Release();
            }
        }

        uint expectedSampleCount =
            FormatHelpers.GetSampleCountUInt32(publicDescription.SampleCount);
        if (actual.Width != expected.Width
            || actual.Height != expected.Height
            || actual.MipLevels != publicDescription.MipLevels
            || actual.ArraySize != expected.ArraySize
            || actual.Format != expected.ResourceFormat
            || actual.SampleDesc.Count != expectedSampleCount
            || actual.SampleDesc.Quality != 0
            || actual.Usage != expected.ResourceUsage
            || actual.BindFlags != (uint)expected.BindFlags
            || actual.CPUAccessFlags != (uint)expected.CpuAccessFlags
            || actual.MiscFlags != (uint)expected.MiscFlags)
        {
            throw new ArgumentException(
                "The native D3D11 texture descriptor does not exactly match the supplied NeoVeldrid texture description.",
                "description");
        }
    }

    private readonly struct NativeTextureDescription
    {
        internal readonly uint Width;
        internal readonly uint Height;
        internal readonly uint ArraySize;
        internal readonly Format ViewFormat;
        internal readonly Format ResourceFormat;
        internal readonly BindFlag BindFlags;
        internal readonly CpuAccessFlag CpuAccessFlags;
        internal readonly Silk.NET.Direct3D11.Usage ResourceUsage;
        internal readonly ResourceMiscFlag MiscFlags;

        private NativeTextureDescription(
            uint width,
            uint height,
            uint arraySize,
            Format viewFormat,
            Format resourceFormat,
            BindFlag bindFlags,
            CpuAccessFlag cpuAccessFlags,
            Silk.NET.Direct3D11.Usage resourceUsage,
            ResourceMiscFlag miscFlags)
        {
            Width = width;
            Height = height;
            ArraySize = arraySize;
            ViewFormat = viewFormat;
            ResourceFormat = resourceFormat;
            BindFlags = bindFlags;
            CpuAccessFlags = cpuAccessFlags;
            ResourceUsage = resourceUsage;
            MiscFlags = miscFlags;
        }

        internal static NativeTextureDescription Create(
            in TextureDescription description)
        {
            bool isDepthStencil =
                (description.Usage & TextureUsage.DepthStencil) != 0;
            Format viewFormat = D3D11Formats.ToDxgiFormat(
                description.Format,
                isDepthStencil);
            Format resourceFormat = D3D11Formats.GetTypelessFormat(viewFormat);
            CpuAccessFlag cpuAccessFlags = CpuAccessFlag.None;
            Silk.NET.Direct3D11.Usage resourceUsage =
                Silk.NET.Direct3D11.Usage.Default;
            BindFlag bindFlags = BindFlag.None;
            ResourceMiscFlag miscFlags = ResourceMiscFlag.None;

            if ((description.Usage & TextureUsage.RenderTarget) != 0)
                bindFlags |= BindFlag.RenderTarget;
            if (isDepthStencil)
                bindFlags |= BindFlag.DepthStencil;
            if ((description.Usage & TextureUsage.Sampled) != 0)
                bindFlags |= BindFlag.ShaderResource;
            if ((description.Usage & TextureUsage.Storage) != 0)
                bindFlags |= BindFlag.UnorderedAccess;
            if ((description.Usage & TextureUsage.Staging) != 0)
            {
                cpuAccessFlags = CpuAccessFlag.Read | CpuAccessFlag.Write;
                resourceUsage = Silk.NET.Direct3D11.Usage.Staging;
            }
            if ((description.Usage & TextureUsage.GenerateMipmaps) != 0)
            {
                bindFlags |= BindFlag.RenderTarget | BindFlag.ShaderResource;
                miscFlags |= ResourceMiscFlag.GenerateMips;
            }

            uint arraySize = description.ArrayLayers;
            if ((description.Usage & TextureUsage.Cubemap) != 0)
            {
                arraySize = checked(arraySize * 6u);
                miscFlags |= ResourceMiscFlag.Texturecube;
            }

            uint width = description.Width;
            uint height = description.Height;
            if (FormatHelpers.IsCompressedFormat(description.Format))
            {
                width = checked(((width + 3u) / 4u) * 4u);
                height = checked(((height + 3u) / 4u) * 4u);
            }

            return new NativeTextureDescription(
                width,
                height,
                arraySize,
                viewFormat,
                resourceFormat,
                bindFlags,
                cpuAccessFlags,
                resourceUsage,
                miscFlags);
        }
    }

    private protected override TextureView CreateFullTextureView(GraphicsDevice gd)
    {
        TextureViewDescription desc = new TextureViewDescription(this);
        D3D11GraphicsDevice d3d11GD = Util.AssertSubtype<GraphicsDevice, D3D11GraphicsDevice>(gd);
        return new D3D11TextureView(d3d11GD, ref desc);
    }

    public override string Name
    {
        get => _name;
        set
        {
            _name = value;
            D3D11Util.SetDebugName((ID3D11DeviceChild*)_deviceTexture.Handle, value);
        }
    }

    private protected override void DisposeCore()
    {
        if (!_disposed)
        {
            _deviceTexture.Dispose();
            _disposed = true;
        }
    }
}
