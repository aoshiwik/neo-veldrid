using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace NeoVeldrid.D3D11;

internal static unsafe class D3D11Util
{
    /// <summary>Sets the debug name on a D3D11 device child via SetPrivateData.</summary>
    internal static void SetDebugName(ID3D11DeviceChild* deviceChild, string name)
    {
        if (deviceChild == null) return;

        // WKPDID_D3DDebugObjectName = {429b8c22-9188-4b0c-8742-acb0bf85c200}
        Guid debugNameGuid = new Guid(0x429b8c22, 0x9188, 0x4b0c, 0x87, 0x42, 0xac, 0xb0, 0xbf, 0x85, 0xc2, 0x00);

        if (string.IsNullOrEmpty(name))
        {
            deviceChild->SetPrivateData(&debugNameGuid, 0, null);
        }
        else
        {
            nint namePtr = Marshal.StringToHGlobalAnsi(name);
            deviceChild->SetPrivateData(&debugNameGuid, (uint)name.Length, (void*)namePtr);
            Marshal.FreeHGlobal(namePtr);
        }
    }

    public static int ComputeSubresource(uint mipLevel, uint mipLevelCount, uint arrayLayer)
    {
        return (int)((arrayLayer * mipLevelCount) + mipLevel);
    }

    internal static Box GetTextureRegion(
        D3D11Texture texture,
        uint x,
        uint y,
        uint z,
        uint width,
        uint height,
        uint depth,
        uint mipLevel)
    {
        uint right = checked(x + width);
        uint bottom = checked(y + height);
        if (FormatHelpers.IsCompressedFormat(texture.Format))
        {
            // D3D11 resources use block-padded top-level dimensions. Tiny and
            // odd logical mip edges therefore do not necessarily coincide
            // with the physical resource edge. Expand an edge block to the
            // block boundary, then clamp it to the physical mip dimension.
            // Callers identify a resulting whole-subresource region and pass
            // a null native box; D3D11 otherwise rejects an explicit box whose
            // tiny physical edge is smaller than one block.
            GetTextureSubresourceStorageDimensions(
                texture,
                mipLevel,
                out uint physicalMipWidth,
                out uint physicalMipHeight,
                out _);
            right = Math.Min(RoundUpToBlockExtent(right), physicalMipWidth);
            bottom = Math.Min(RoundUpToBlockExtent(bottom), physicalMipHeight);
        }

        return new Box
        {
            Left = x,
            Top = y,
            Front = z,
            Right = right,
            Bottom = bottom,
            Back = checked(z + depth)
        };
    }

    internal static void GetTextureSubresourceStorageDimensions(
        D3D11Texture texture,
        uint mipLevel,
        out uint width,
        out uint height,
        out uint depth)
    {
        uint topLevelWidth = texture.Width;
        uint topLevelHeight = texture.Height;
        if (FormatHelpers.IsCompressedFormat(texture.Format))
        {
            topLevelWidth = RoundUpToBlockExtent(topLevelWidth);
            topLevelHeight = RoundUpToBlockExtent(topLevelHeight);
        }

        width = Util.GetDimension(topLevelWidth, mipLevel);
        height = Util.GetDimension(topLevelHeight, mipLevel);
        depth = Util.GetDimension(texture.Depth, mipLevel);
    }

    internal static bool IsFullTextureSubresource(
        D3D11Texture texture,
        uint mipLevel,
        in Box region)
    {
        GetTextureSubresourceStorageDimensions(
            texture,
            mipLevel,
            out uint width,
            out uint height,
            out uint depth);
        return region.Left == 0u
            && region.Top == 0u
            && region.Front == 0u
            && region.Right == width
            && region.Bottom == height
            && region.Back == depth;
    }

    private static uint RoundUpToBlockExtent(uint value)
    {
        const ulong blockExtent = 4u;
        return checked((uint)(((ulong)value + blockExtent - 1u) /
            blockExtent * blockExtent));
    }

    internal static ShaderResourceViewDesc GetSrvDesc(
        D3D11Texture tex,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        PixelFormat format)
    {
        ShaderResourceViewDesc srvDesc = new ShaderResourceViewDesc();
        srvDesc.Format = D3D11Formats.GetViewFormat(
            D3D11Formats.ToDxgiFormat(format, (tex.Usage & TextureUsage.DepthStencil) != 0));

        if ((tex.Usage & TextureUsage.Cubemap) == TextureUsage.Cubemap)
        {
            if (tex.ArrayLayers == 1)
            {
                srvDesc.ViewDimension = D3DSrvDimension.D3DSrvDimensionTexturecube;
                srvDesc.TextureCube.MostDetailedMip = baseMipLevel;
                srvDesc.TextureCube.MipLevels = levelCount;
            }
            else
            {
                srvDesc.ViewDimension = D3DSrvDimension.D3DSrvDimensionTexturecubearray;
                srvDesc.TextureCubeArray.MostDetailedMip = baseMipLevel;
                srvDesc.TextureCubeArray.MipLevels = levelCount;
                srvDesc.TextureCubeArray.First2DArrayFace = baseArrayLayer;
                srvDesc.TextureCubeArray.NumCubes = tex.ArrayLayers;
            }
        }
        else if (tex.Depth == 1)
        {
            if (tex.ArrayLayers == 1)
            {
                if (tex.Type == TextureType.Texture1D)
                {
                    srvDesc.ViewDimension = D3DSrvDimension.D3DSrvDimensionTexture1D;
                    srvDesc.Texture1D.MostDetailedMip = baseMipLevel;
                    srvDesc.Texture1D.MipLevels = levelCount;
                }
                else
                {
                    if (tex.SampleCount == TextureSampleCount.Count1)
                        srvDesc.ViewDimension = D3DSrvDimension.D3DSrvDimensionTexture2D;
                    else
                        srvDesc.ViewDimension = D3DSrvDimension.D3DSrvDimensionTexture2Dms;
                    srvDesc.Texture2D.MostDetailedMip = baseMipLevel;
                    srvDesc.Texture2D.MipLevels = levelCount;
                }
            }
            else
            {
                if (tex.Type == TextureType.Texture1D)
                {
                    srvDesc.ViewDimension = D3DSrvDimension.D3DSrvDimensionTexture1Darray;
                    srvDesc.Texture1DArray.MostDetailedMip = baseMipLevel;
                    srvDesc.Texture1DArray.MipLevels = levelCount;
                    srvDesc.Texture1DArray.FirstArraySlice = baseArrayLayer;
                    srvDesc.Texture1DArray.ArraySize = layerCount;
                }
                else
                {
                    srvDesc.ViewDimension = D3DSrvDimension.D3DSrvDimensionTexture2Darray;
                    srvDesc.Texture2DArray.MostDetailedMip = baseMipLevel;
                    srvDesc.Texture2DArray.MipLevels = levelCount;
                    srvDesc.Texture2DArray.FirstArraySlice = baseArrayLayer;
                    srvDesc.Texture2DArray.ArraySize = layerCount;
                }
            }
        }
        else
        {
            srvDesc.ViewDimension = D3DSrvDimension.D3DSrvDimensionTexture3D;
            srvDesc.Texture3D.MostDetailedMip = baseMipLevel;
            srvDesc.Texture3D.MipLevels = levelCount;
        }

        return srvDesc;
    }

    internal static int GetSyncInterval(bool syncToVBlank)
    {
        return syncToVBlank ? 1 : 0;
    }
}
