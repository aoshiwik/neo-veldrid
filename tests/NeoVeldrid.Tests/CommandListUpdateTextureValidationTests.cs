using System;
using Xunit;

namespace NeoVeldrid.Tests;

public sealed class CommandListUpdateTextureValidationTests
{
    private static readonly IntPtr Source = new IntPtr(1);

    [Fact]
    public void ExactSelectedMipRegionIsAccepted()
    {
        var texture = new StubTexture(
            width: 16,
            height: 16,
            mipLevels: 3,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled);

        CommandList.ValidateUpdateTextureParameters(
            texture,
            Source,
            sizeInBytes: 16,
            x: 0,
            y: 0,
            z: 0,
            width: 4,
            height: 4,
            depth: 1,
            mipLevel: 2,
            arrayLayer: 0);
    }

    [Theory]
    [InlineData(15u)]
    [InlineData(17u)]
    public void TextureUploadSizeMustExactlyMatchRegion(uint sizeInBytes)
    {
        var texture = new StubTexture(
            width: 4,
            height: 4,
            mipLevels: 1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled);

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                Source,
                sizeInBytes,
                0,
                0,
                0,
                4,
                4,
                1,
                0,
                0));

        Assert.Contains("exactly match", exception.Message);
    }

    [Fact]
    public void ImmediateAndRecordedTextureUploadsHaveExplicitPayloadContracts()
    {
        var texture = new StubTexture(
            width: 4,
            height: 4,
            mipLevels: 1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled);

        Assert.Null(Record.Exception(
            () => GraphicsDevice.ValidateUpdateTextureParameters(
                texture,
                sizeInBytes: 17,
                x: 0,
                y: 0,
                z: 0,
                width: 4,
                height: 4,
                depth: 1,
                mipLevel: 0,
                arrayLayer: 0)));
        Assert.Throws<NeoVeldridException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                Source,
                sizeInBytes: 17,
                x: 0,
                y: 0,
                z: 0,
                width: 4,
                height: 4,
                depth: 1,
                mipLevel: 0,
                arrayLayer: 0));
    }

    [Fact]
    public void ImmediateCompressedUploadRetainsBlockPaddedTinyMipContract()
    {
        var texture = new StubTexture(
            width: 2,
            height: 2,
            mipLevels: 1,
            PixelFormat.BC1_Rgb_UNorm,
            TextureUsage.Sampled);

        Assert.Null(Record.Exception(
            () => GraphicsDevice.ValidateUpdateTextureParameters(
                texture,
                sizeInBytes: 16,
                x: 0,
                y: 0,
                z: 0,
                width: 4,
                height: 4,
                depth: 1,
                mipLevel: 0,
                arrayLayer: 0)));
        Assert.Throws<NeoVeldridException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                Source,
                sizeInBytes: 8,
                x: 0,
                y: 0,
                z: 0,
                width: 4,
                height: 4,
                depth: 1,
                mipLevel: 0,
                arrayLayer: 0));
    }

    [Fact]
    public void SelectedMipBoundsAreAuthoritative()
    {
        var texture = new StubTexture(
            width: 16,
            height: 16,
            mipLevels: 3,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled);

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                Source,
                sizeInBytes: 4,
                x: 4,
                y: 0,
                z: 0,
                width: 1,
                height: 4,
                depth: 1,
                mipLevel: 2,
                arrayLayer: 0));

        Assert.Contains("selected Texture mip level", exception.Message);
    }

    [Fact]
    public void ArrayLayerBoundsAreAuthoritative()
    {
        var texture = new StubTexture(
            width: 4,
            height: 4,
            mipLevels: 1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled,
            arrayLayers: 2);

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                Source,
                sizeInBytes: 16,
                x: 0,
                y: 0,
                z: 0,
                width: 4,
                height: 4,
                depth: 1,
                mipLevel: 0,
                arrayLayer: 2));

        Assert.Contains("effective array layer count", exception.Message);
    }

    [Fact]
    public void CompressedEdgeRegionUsesRoundedBlockStorage()
    {
        var texture = new StubTexture(
            width: 10,
            height: 10,
            mipLevels: 1,
            PixelFormat.BC3_UNorm,
            TextureUsage.Sampled);

        CommandList.ValidateUpdateTextureParameters(
            texture,
            Source,
            sizeInBytes: 64,
            x: 4,
            y: 4,
            z: 0,
            width: 6,
            height: 6,
            depth: 1,
            mipLevel: 0,
            arrayLayer: 0);

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                Source,
                sizeInBytes: 64,
                x: 2,
                y: 4,
                z: 0,
                width: 8,
                height: 6,
                depth: 1,
                mipLevel: 0,
                arrayLayer: 0));
        Assert.Contains("block-aligned", exception.Message);
    }

    [Fact]
    public void NullSourceIsRejectedBeforeBackendRecording()
    {
        var texture = CreateColorTexture();

        Assert.Throws<ArgumentNullException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                IntPtr.Zero,
                16,
                0,
                0,
                0,
                4,
                4,
                1,
                0,
                0));
    }

    [Fact]
    public void TextureUpdateByteCountOverflowIsRejected()
    {
        var texture = new StubTexture(
            width: uint.MaxValue,
            height: uint.MaxValue,
            mipLevels: 1,
            PixelFormat.R32_G32_B32_A32_Float,
            TextureUsage.Sampled,
            depth: uint.MaxValue);

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                Source,
                uint.MaxValue,
                0,
                0,
                0,
                uint.MaxValue,
                uint.MaxValue,
                uint.MaxValue,
                0,
                0));

        Assert.Contains("UInt64.MaxValue", exception.Message);
    }

    [Fact]
    public void CubemapLayerExpansionOverflowIsRejected()
    {
        var texture = new StubTexture(
            width: 1,
            height: 1,
            mipLevels: 1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled | TextureUsage.Cubemap,
            arrayLayers: uint.MaxValue);

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                Source,
                1,
                0,
                0,
                0,
                1,
                1,
                1,
                0,
                0));

        Assert.Contains("effective array layer count", exception.Message);
    }

    [Theory]
    [InlineData(TextureUsage.Staging, PixelFormat.R8_UNorm, TextureSampleCount.Count1)]
    [InlineData(TextureUsage.DepthStencil, PixelFormat.R16_UNorm, TextureSampleCount.Count1)]
    [InlineData(TextureUsage.Sampled, PixelFormat.D24_UNorm_S8_UInt, TextureSampleCount.Count1)]
    [InlineData(TextureUsage.Sampled, PixelFormat.R8_UNorm, TextureSampleCount.Count4)]
    public void UnsupportedDestinationClassesAreRejected(
        TextureUsage usage,
        PixelFormat format,
        TextureSampleCount sampleCount)
    {
        var texture = new StubTexture(
            width: 4,
            height: 4,
            mipLevels: 1,
            format,
            usage,
            sampleCount);

        Assert.Throws<NeoVeldridException>(
            () => CommandList.ValidateUpdateTextureParameters(
                texture,
                Source,
                16,
                0,
                0,
                0,
                4,
                4,
                1,
                0,
                0));
    }

    private static StubTexture CreateColorTexture()
        => new(
            width: 4,
            height: 4,
            mipLevels: 1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled);

    private sealed class StubTexture : Texture
    {
        private bool disposed;

        public StubTexture(
            uint width,
            uint height,
            uint mipLevels,
            PixelFormat format,
            TextureUsage usage,
            TextureSampleCount sampleCount = TextureSampleCount.Count1,
            uint depth = 1,
            uint arrayLayers = 1)
        {
            Width = width;
            Height = height;
            Depth = depth;
            MipLevels = mipLevels;
            ArrayLayers = arrayLayers;
            Format = format;
            Usage = usage;
            SampleCount = sampleCount;
        }

        public override PixelFormat Format { get; }
        public override uint Width { get; }
        public override uint Height { get; }
        public override uint Depth { get; }
        public override uint MipLevels { get; }
        public override uint ArrayLayers { get; }
        public override TextureUsage Usage { get; }
        public override TextureType Type =>
            Depth > 1 ? TextureType.Texture3D : TextureType.Texture2D;
        public override TextureSampleCount SampleCount { get; }
        public override string Name { get; set; } = "validation-texture";
        public override bool IsDisposed => disposed;

        private protected override void DisposeCore()
        {
            disposed = true;
        }
    }
}
