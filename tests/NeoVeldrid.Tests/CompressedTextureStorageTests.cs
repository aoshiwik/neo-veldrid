using System;
using Xunit;

namespace NeoVeldrid.Tests;

public sealed class CompressedTextureStorageTests
{
    [Theory]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 13u, 11u, 1u, 96u)]
    [InlineData(PixelFormat.BC3_UNorm, 13u, 11u, 1u, 192u)]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 3u, 2u, 1u, 8u)]
    [InlineData(PixelFormat.BC3_UNorm, 3u, 2u, 1u, 16u)]
    public void RegionSizeUsesCeilingBlockDimensions(
        PixelFormat format,
        uint width,
        uint height,
        uint depth,
        uint expectedSize)
    {
        Assert.Equal(
            expectedSize,
            FormatHelpers.GetRegionSize(width, height, depth, format));
    }

    [Fact]
    public unsafe void RegionCopyIncludesOddWidthFinalBlock()
    {
        const uint sourceRowPitch = 24;
        const uint sourceDepthPitch = 48;
        const uint destinationRowPitch = 32;
        const uint destinationDepthPitch = 64;
        const byte untouched = 0xCC;

        byte[] source = new byte[checked((int)sourceDepthPitch)];
        for (int index = 0; index < source.Length; index++)
            source[index] = checked((byte)(17 + index));
        byte[] destination = new byte[checked((int)destinationDepthPitch)];
        Array.Fill(destination, untouched);

        fixed (byte* sourcePointer = source)
        fixed (byte* destinationPointer = destination)
        {
            Util.CopyTextureRegion(
                sourcePointer,
                0, 0, 0,
                sourceRowPitch,
                sourceDepthPitch,
                destinationPointer,
                0, 0, 0,
                destinationRowPitch,
                destinationDepthPitch,
                width: 9,
                height: 7,
                depth: 1,
                PixelFormat.BC1_Rgba_UNorm);
        }

        for (uint row = 0; row < 2; row++)
        {
            for (uint column = 0; column < sourceRowPitch; column++)
            {
                Assert.Equal(
                    source[checked((int)(row * sourceRowPitch + column))],
                    destination[checked((int)(row * destinationRowPitch + column))]);
            }

            for (uint column = sourceRowPitch;
                column < destinationRowPitch;
                column++)
            {
                Assert.Equal(
                    untouched,
                    destination[checked((int)(row * destinationRowPitch + column))]);
            }
        }
    }

    [Fact]
    public unsafe void RegionCopyHonorsOffsetsWhenPitchesMatch()
    {
        const uint rowPitch = 16;
        const uint depthPitch = 32;
        const byte untouched = 0xCC;
        byte[] source = new byte[checked((int)(depthPitch * 3))];
        for (int index = 0; index < source.Length; index++)
            source[index] = checked((byte)(17 + index));
        byte[] destination = new byte[source.Length];
        Array.Fill(destination, untouched);

        fixed (byte* sourcePointer = source)
        fixed (byte* destinationPointer = destination)
        {
            Util.CopyTextureRegion(
                sourcePointer,
                srcX: 4, srcY: 4, srcZ: 1,
                rowPitch,
                depthPitch,
                destinationPointer,
                dstX: 4, dstY: 0, dstZ: 2,
                rowPitch,
                depthPitch,
                width: 4,
                height: 4,
                depth: 1,
                PixelFormat.BC1_Rgba_UNorm);
        }

        const int sourceOffset = 56;
        const int destinationOffset = 72;
        const int copiedBlockSize = 8;
        for (int index = 0; index < destination.Length; index++)
        {
            byte expected = index >= destinationOffset
                && index < destinationOffset + copiedBlockSize
                ? source[sourceOffset + index - destinationOffset]
                : untouched;
            Assert.Equal(expected, destination[index]);
        }
    }

    [Fact]
    public unsafe void RegionCopyWholeSlicesHonorsNonzeroDepthOffsets()
    {
        const uint rowPitch = 16;
        const uint depthPitch = 32;
        const uint sliceCount = 5;
        const uint sourceZ = 1;
        const uint destinationZ = 2;
        const uint copyDepth = 2;
        const byte untouched = 0xA5;
        byte[] source = new byte[checked((int)(depthPitch * sliceCount))];
        for (int index = 0; index < source.Length; index++)
            source[index] = unchecked((byte)(0x31 + index * 7));
        byte[] destination = new byte[source.Length];
        Array.Fill(destination, untouched);

        fixed (byte* sourcePointer = source)
        fixed (byte* destinationPointer = destination)
        {
            Util.CopyTextureRegion(
                sourcePointer,
                0, 0, sourceZ,
                rowPitch,
                depthPitch,
                destinationPointer,
                0, 0, destinationZ,
                rowPitch,
                depthPitch,
                width: 8,
                height: 8,
                depth: copyDepth,
                PixelFormat.BC1_Rgba_UNorm);
        }

        for (uint slice = 0; slice < sliceCount; slice++)
        {
            for (uint offset = 0; offset < depthPitch; offset++)
            {
                int destinationIndex = checked((int)(
                    slice * depthPitch + offset));
                byte expected = slice >= destinationZ
                    && slice < destinationZ + copyDepth
                    ? source[checked((int)(
                        (sourceZ + slice - destinationZ) * depthPitch
                        + offset))]
                    : untouched;
                Assert.Equal(expected, destination[destinationIndex]);
            }
        }
    }

    [Theory]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 144u, 288u, 272u, 8u)]
    [InlineData(PixelFormat.BC3_UNorm, 288u, 576u, 544u, 16u)]
    public void StagingLayoutAccountsForOddMipsAndArrayLayerOne(
        PixelFormat format,
        uint expectedLayerPitch,
        uint expectedTotalSize,
        uint expectedMipTwoLayerOneOffset,
        uint expectedMipTwoSize)
    {
        TextureStagingLayout layout = TextureStagingLayout.Create(
            width: 13,
            height: 11,
            depth: 1,
            mipLevels: 4,
            arrayLayers: 2,
            format);

        Assert.Equal(expectedTotalSize, layout.TotalSizeInBytes);

        StagingTextureSubresourceLayout mipTwoLayerOne =
            layout.GetSubresourceLayout(mipLevel: 2, arrayLayer: 1);
        Assert.Equal(expectedMipTwoLayerOneOffset, mipTwoLayerOne.Offset);
        Assert.Equal(expectedMipTwoSize, mipTwoLayerOne.SizeInBytes);
        Assert.Equal(expectedMipTwoSize, mipTwoLayerOne.RowPitch);
        Assert.Equal(expectedMipTwoSize, mipTwoLayerOne.DepthPitch);
        Assert.Equal(expectedLayerPitch, mipTwoLayerOne.ArrayPitch);
    }

    [Theory]
    [InlineData(65_536u, 65_536u, 1u, 1u)]
    [InlineData(65_536u, 32_768u, 1u, 2u)]
    public void StagingLayoutRejectsMoreThanUInt32AddressableBytes(
        uint width,
        uint height,
        uint mipLevels,
        uint arrayLayers)
    {
        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => TextureStagingLayout.Create(
                width,
                height,
                depth: 1,
                mipLevels,
                arrayLayers,
                PixelFormat.R8_UNorm));

        Assert.Contains("UInt32 byte-addressable", exception.Message);
    }

    [Theory]
    [InlineData(
        PixelFormat.BC1_Rgba_UNorm,
        13u, 11u, 5u,
        9u, 7u, 3u,
        2u,
        32u, 96u, 480u,
        24u, 48u, 144u,
        960u)]
    [InlineData(
        PixelFormat.BC3_UNorm,
        13u, 11u, 2u,
        3u, 2u, 2u,
        3u,
        64u, 192u, 384u,
        16u, 16u, 32u,
        1152u)]
    public void OpenGLCompressedCopyLayoutPreservesDepthAndArrayStride(
        PixelFormat format,
        uint mipWidth,
        uint mipHeight,
        uint mipDepth,
        uint copyWidth,
        uint copyHeight,
        uint copyDepth,
        uint sourceArrayLayer,
        uint expectedFullRowPitch,
        uint expectedFullDepthPitch,
        uint expectedFullArrayPitch,
        uint expectedDenseRowPitch,
        uint expectedDenseDepthPitch,
        uint expectedDenseCopySize,
        uint expectedSourceArrayLayerOffset)
    {
        CompressedTextureCopyLayout layout = CompressedTextureCopyLayout.Create(
            mipWidth,
            mipHeight,
            mipDepth,
            copyWidth,
            copyHeight,
            copyDepth,
            sourceArrayLayer,
            format);

        Assert.Equal(expectedFullRowPitch, layout.FullRowPitch);
        Assert.Equal(expectedFullDepthPitch, layout.FullDepthPitch);
        Assert.Equal(expectedFullArrayPitch, layout.FullArrayPitch);
        Assert.Equal(expectedDenseRowPitch, layout.DenseRowPitch);
        Assert.Equal(expectedDenseDepthPitch, layout.DenseDepthPitch);
        Assert.Equal(expectedDenseCopySize, layout.DenseCopySizeInBytes);
        Assert.Equal(
            expectedSourceArrayLayerOffset,
            layout.SourceArrayLayerOffset);
    }

    [Fact]
    public void OpenGLCompressedCopyLayoutRejectsOverflowingDenseDepth()
    {
        Assert.Throws<OverflowException>(() =>
            CompressedTextureCopyLayout.Create(
                mipWidth: 4,
                mipHeight: 4,
                mipDepth: 1,
                copyWidth: 4,
                copyHeight: 4,
                copyDepth: uint.MaxValue,
                sourceArrayLayer: 0,
                PixelFormat.BC1_Rgba_UNorm));
    }

    [Fact]
    public void OpenGLCompressedCopyLayoutRejectsOverflowingArrayOffset()
    {
        Assert.Throws<OverflowException>(() =>
            CompressedTextureCopyLayout.Create(
                mipWidth: 4,
                mipHeight: 4,
                mipDepth: 2,
                copyWidth: 4,
                copyHeight: 4,
                copyDepth: 1,
                sourceArrayLayer: uint.MaxValue,
                PixelFormat.BC1_Rgba_UNorm));
    }
}
