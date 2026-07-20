using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace NeoVeldrid.Tests;

public abstract partial class TextureTestBase<T> : GraphicsDeviceTestBase<T> where T : GraphicsDeviceCreator
{
    [Fact]
    public void CommandListTextureUpdateValidationIsBackendIndependent()
    {
        Texture texture = RF.CreateTexture(
            TextureDescription.Texture2D(
                4,
                4,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Sampled));
        CommandList commandList = RF.CreateCommandList();
        byte[] undersizedSource = new byte[15];

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => commandList.UpdateTexture(
                texture,
                undersizedSource,
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
    public unsafe void CommandListTextureUpdatesAreOrderedInSingleSubmission()
    {
        const uint textureSize = 8;
        Texture destination = RF.CreateTexture(
            TextureDescription.Texture2D(
                textureSize,
                textureSize,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Sampled));
        Texture capture = RF.CreateTexture(
            TextureDescription.Texture2D(
                textureSize,
                textureSize,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Staging));
        byte[] initial = new byte[checked((int)(textureSize * textureSize))];
        byte[] patch = { 11, 22, 33, 44, 55, 66 };
        CommandList commandList = RF.CreateCommandList();
        commandList.EnableSubmissionDiagnostics(
            initialBufferAccessCapacity: 2);

        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            initial,
            0,
            0,
            0,
            textureSize,
            textureSize,
            1,
            0,
            0);
        commandList.UpdateTexture(
            destination,
            patch,
            x: 2,
            y: 3,
            z: 0,
            width: 3,
            height: 2,
            depth: 1,
            mipLevel: 0,
            arrayLayer: 0);
        commandList.CopyTexture(destination, capture);
        commandList.End();

        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.True(commandList.TryGetLastSubmissionMetrics(
            out CommandListSubmissionMetrics metrics));
        Assert.Equal(1L, metrics.SubmissionSequence);
        Assert.Equal(2, metrics.UpdateTextureCallCount);
        Assert.Equal(70UL, metrics.UpdatedTextureBytes);
        Assert.Equal(1, metrics.CopyTextureCallCount);

        MappedResource mapped = GD.Map(capture, MapMode.Read);
        try
        {
            byte* basePointer = (byte*)mapped.Data;
            for (uint y = 0; y < textureSize; y++)
            {
                for (uint x = 0; x < textureSize; x++)
                {
                    int patchX = checked((int)x - 2);
                    int patchY = checked((int)y - 3);
                    byte expected =
                        patchX >= 0 && patchX < 3 &&
                        patchY >= 0 && patchY < 2
                            ? patch[(patchY * 3) + patchX]
                            : (byte)0;
                    nuint byteOffset = checked(
                        ((nuint)y * mapped.RowPitch) + x);
                    byte actual = *(basePointer + checked((nint)byteOffset));
                    Assert.Equal(expected, actual);
                }
            }
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    [Fact]
    public unsafe void CommandListTextureUpdateRetainsArrayMipPayloadAtRecordTime()
    {
        const uint textureSize = 8;
        const uint mipLevel = 1;
        const uint arrayLayer = 1;
        const uint mipSize = textureSize >> (int)mipLevel;
        TextureDescription description = TextureDescription.Texture2D(
            textureSize,
            textureSize,
            3,
            2,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled);
        Texture destination = RF.CreateTexture(description);
        description.Usage = TextureUsage.Staging;
        Texture capture = RF.CreateTexture(description);
        byte[] source = new byte[checked((int)(mipSize * mipSize))];
        for (int i = 0; i < source.Length; i++)
            source[i] = checked((byte)(31 + i));
        byte[] expected = source.ToArray();
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            source,
            0,
            0,
            0,
            mipSize,
            mipSize,
            1,
            mipLevel,
            arrayLayer);
        Array.Fill(source, (byte)0xEE);
        commandList.CopyTexture(
            destination, 0, 0, 0, mipLevel, arrayLayer,
            capture, 0, 0, 0, mipLevel, arrayLayer,
            mipSize, mipSize, 1, 1);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        uint subresource = capture.CalculateSubresource(mipLevel, arrayLayer);
        MappedResource mapped = GD.Map(capture, MapMode.Read, subresource);
        try
        {
            byte* basePointer = (byte*)mapped.Data;
            for (uint y = 0; y < mipSize; y++)
            {
                for (uint x = 0; x < mipSize; x++)
                {
                    byte actual = *(basePointer + checked((nint)(y * mapped.RowPitch + x)));
                    Assert.Equal(expected[checked((int)(y * mipSize + x))], actual);
                }
            }
        }
        finally
        {
            GD.Unmap(capture, subresource);
        }
    }

    [SkippableFact]
    public unsafe void CommandListCompressedEdgeMipUploadRetainsPayload()
    {
        const PixelFormat format = PixelFormat.BC1_Rgba_UNorm;
        Skip.IfNot(
            GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Sampled) &&
            GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Staging),
            $"NV-SKIP-COMPRESSED-STAGING: {format} compressed staging readback is unavailable on {GD.BackendType}.");

        TextureDescription description = TextureDescription.Texture2D(
            7,
            5,
            2,
            1,
            format,
            TextureUsage.Sampled);
        Texture destination = RF.CreateTexture(description);
        description.Usage = TextureUsage.Staging;
        Texture capture = RF.CreateTexture(description);
        byte[] source = { 3, 5, 8, 13, 21, 34, 55, 89 };
        byte[] expected = source.ToArray();
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            source,
            0,
            0,
            0,
            3,
            2,
            1,
            1,
            0);
        Array.Fill(source, (byte)0xCC);
        commandList.CopyTexture(
            destination, 0, 0, 0, 1, 0,
            capture, 0, 0, 0, 1, 0,
            3, 2, 1, 1);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        uint subresource = capture.CalculateSubresource(1, 0);
        MappedResourceView<byte> mapped = GD.Map<byte>(capture, MapMode.Read, subresource);
        try
        {
            for (uint i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], mapped[i]);
        }
        finally
        {
            GD.Unmap(capture, subresource);
        }
    }

    [SkippableFact]
    public unsafe void CommandListCompressedOffsetEdgeUploadRetainsPayload()
    {
        const PixelFormat format = PixelFormat.BC1_Rgba_UNorm;
        Skip.IfNot(
            GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Sampled) &&
            GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Staging),
            $"NV-SKIP-COMPRESSED-STAGING: {format} compressed staging readback is unavailable on {GD.BackendType}.");

        TextureDescription description = TextureDescription.Texture2D(
            10,
            10,
            1,
            1,
            format,
            TextureUsage.Sampled);
        Texture destination = RF.CreateTexture(description);
        description.Usage = TextureUsage.Staging;
        Texture capture = RF.CreateTexture(description);
        byte[] initial = new byte[3 * 3 * 8];
        byte[] source = Enumerable.Range(0, 4 * 8)
            .Select(index => checked((byte)(17 + index)))
            .ToArray();
        byte[] expected = source.ToArray();
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            initial,
            0, 0, 0,
            10, 10, 1,
            0, 0);
        commandList.UpdateTexture(
            destination,
            source,
            4, 4, 0,
            6, 6, 1,
            0, 0);
        Array.Fill(source, (byte)0xCC);
        commandList.CopyTexture(destination, capture);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        MappedResource mapped = GD.Map(capture, MapMode.Read);
        try
        {
            byte* basePointer = (byte*)mapped.Data;
            for (uint blockY = 0; blockY < 2; blockY++)
            {
                for (uint blockX = 0; blockX < 2; blockX++)
                {
                    for (uint byteInBlock = 0; byteInBlock < 8; byteInBlock++)
                    {
                        uint expectedOffset =
                            ((blockY * 2u + blockX) * 8u) + byteInBlock;
                        nuint actualOffset = checked(
                            ((nuint)(blockY + 1u) * mapped.RowPitch) +
                            ((nuint)(blockX + 1u) * 8u) +
                            byteInBlock);
                        Assert.Equal(
                            expected[expectedOffset],
                            *(basePointer + checked((nint)actualOffset)));
                    }
                }
            }
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    [SkippableTheory]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 8u)]
    [InlineData(PixelFormat.BC3_UNorm, 16u)]
    public unsafe void Copy_Compressed_StagingDirectionsHaveIndependentOracles(
        PixelFormat format,
        uint blockSizeInBytes)
    {
        Skip.IfNot(
            GD.GetPixelFormatSupport(
                format,
                TextureType.Texture2D,
                TextureUsage.Sampled),
            $"NV-SKIP-COMPRESSED-SAMPLING: {format} sampling is unavailable on {GD.BackendType}.");
        Skip.IfNot(
            GD.GetPixelFormatSupport(
                format,
                TextureType.Texture2D,
                TextureUsage.Staging),
            $"NV-SKIP-COMPRESSED-STAGING: {format} staging-to-device and device-to-staging copies are unavailable on {GD.BackendType} because compressed staging textures are unsupported.");

        CompressedCopyRegion[] regions =
        {
            // The top-level dimensions are intentionally odd. The region is
            // block-aligned at a nonzero offset and ends at the logical edge.
            new CompressedCopyRegion(
                mipLevel: 0,
                arrayLayer: 1,
                x: 4,
                y: 4,
                width: 9,
                height: 7,
                patternSeed: 0x31),
            // Mip two is 3x2 logical texels but still occupies one complete
            // block. Both copies target layer one so broken layer pitches read
            // or write the untouched layer-zero storage instead.
            new CompressedCopyRegion(
                mipLevel: 2,
                arrayLayer: 1,
                x: 0,
                y: 0,
                width: 3,
                height: 2,
                patternSeed: 0xA7),
        };

        AssertCompressedOptimalToStagingCopy(
            format,
            blockSizeInBytes,
            textureWidth: 13,
            textureHeight: 11,
            mipLevels: 4,
            arrayLayers: 2,
            regions);
        AssertCompressedStagingToOptimalCopy(
            format,
            blockSizeInBytes,
            textureWidth: 13,
            textureHeight: 11,
            mipLevels: 4,
            arrayLayers: 2,
            regions);
    }

    protected readonly struct CompressedCopyRegion
    {
        private const uint BlockExtent = 4;

        public uint MipLevel { get; }
        public uint ArrayLayer { get; }
        public uint X { get; }
        public uint Y { get; }
        public uint Width { get; }
        public uint Height { get; }
        public byte PatternSeed { get; }
        public uint FirstBlockX => X / BlockExtent;
        public uint FirstBlockY => Y / BlockExtent;
        public uint BlockColumns => DivideRoundUp(Width, BlockExtent);
        public uint BlockRows => DivideRoundUp(Height, BlockExtent);

        public CompressedCopyRegion(
            uint mipLevel,
            uint arrayLayer,
            uint x,
            uint y,
            uint width,
            uint height,
            byte patternSeed)
        {
            MipLevel = mipLevel;
            ArrayLayer = arrayLayer;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            PatternSeed = patternSeed;
        }
    }

    protected unsafe void AssertCompressedOptimalToStagingCopy(
        PixelFormat format,
        uint blockSizeInBytes,
        uint textureWidth,
        uint textureHeight,
        uint mipLevels,
        uint arrayLayers,
        params CompressedCopyRegion[] regions)
    {
        const byte untouched = 0xD6;
        AssertCompressedCopyArguments(format, blockSizeInBytes, regions);

        TextureDescription optimalDescription = TextureDescription.Texture2D(
            textureWidth,
            textureHeight,
            mipLevels,
            arrayLayers,
            format,
            TextureUsage.Sampled);
        Texture source = RF.CreateTexture(optimalDescription);
        optimalDescription.Usage = TextureUsage.Staging;
        Texture capture = RF.CreateTexture(optimalDescription);
        byte[][] sourceMips = new byte[regions.Length][];

        for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
        {
            CompressedCopyRegion region = regions[regionIndex];
            GetAndValidateCompressedRegion(
                source,
                region,
                out uint mipWidth,
                out uint mipHeight);
            byte[] sourceMip = CreateCompressedPattern(
                mipWidth,
                mipHeight,
                blockSizeInBytes,
                region.PatternSeed);
            sourceMips[regionIndex] = sourceMip;
            GD.UpdateTexture(
                source,
                sourceMip,
                0,
                0,
                0,
                mipWidth,
                mipHeight,
                1,
                region.MipLevel,
                region.ArrayLayer);

            uint subresource = capture.CalculateSubresource(
                region.MipLevel,
                region.ArrayLayer);
            MappedResource captureMap = GD.Map(
                capture,
                MapMode.Write,
                subresource);
            try
            {
                FillCompressedMappedMip(
                    captureMap,
                    mipWidth,
                    mipHeight,
                    blockSizeInBytes,
                    untouched);
            }
            finally
            {
                GD.Unmap(capture, subresource);
            }

            uint layerZeroSubresource = capture.CalculateSubresource(
                region.MipLevel,
                0);
            MappedResource layerZeroMap = GD.Map(
                capture,
                MapMode.Write,
                layerZeroSubresource);
            try
            {
                FillCompressedMappedMip(
                    layerZeroMap,
                    mipWidth,
                    mipHeight,
                    blockSizeInBytes,
                    untouched);
            }
            finally
            {
                GD.Unmap(capture, layerZeroSubresource);
            }
        }
        GD.WaitForIdle();

        CommandList commandList = RF.CreateCommandList();
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 2);
        commandList.Begin();
        foreach (CompressedCopyRegion region in regions)
        {
            commandList.CopyTexture(
                source,
                region.X,
                region.Y,
                0,
                region.MipLevel,
                region.ArrayLayer,
                capture,
                region.X,
                region.Y,
                0,
                region.MipLevel,
                region.ArrayLayer,
                region.Width,
                region.Height,
                1,
                1);
        }
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.True(commandList.TryGetLastSubmissionMetrics(
            out CommandListSubmissionMetrics metrics));
        Assert.Equal(regions.Length, metrics.CopyTextureCallCount);

        for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
        {
            AssertCompressedMipEqualsSourceRegionAndSentinel(
                capture,
                regions[regionIndex],
                sourceMips[regionIndex],
                blockSizeInBytes,
                untouched,
                "optimal-to-staging");
            AssertCompressedMipIsSentinel(
                capture,
                regions[regionIndex].MipLevel,
                arrayLayer: 0,
                blockSizeInBytes,
                untouched,
                "optimal-to-staging layer isolation");
        }
    }

    protected unsafe void AssertCompressedStagingToOptimalCopy(
        PixelFormat format,
        uint blockSizeInBytes,
        uint textureWidth,
        uint textureHeight,
        uint mipLevels,
        uint arrayLayers,
        params CompressedCopyRegion[] regions)
    {
        const byte untouched = 0x6D;
        AssertCompressedCopyArguments(format, blockSizeInBytes, regions);

        TextureDescription stagingDescription = TextureDescription.Texture2D(
            textureWidth,
            textureHeight,
            mipLevels,
            arrayLayers,
            format,
            TextureUsage.Staging);
        Texture source = RF.CreateTexture(stagingDescription);
        Texture capture = RF.CreateTexture(stagingDescription);
        stagingDescription.Usage = TextureUsage.Sampled;
        Texture destination = RF.CreateTexture(stagingDescription);
        byte[][] expectedRegions = new byte[regions.Length][];

        for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
        {
            CompressedCopyRegion region = regions[regionIndex];
            GetAndValidateCompressedRegion(
                destination,
                region,
                out uint mipWidth,
                out uint mipHeight);
            byte[] destinationSentinel = new byte[checked((int)
                FormatHelpers.GetRegionSize(
                    mipWidth,
                    mipHeight,
                    1,
                    format))];
            Array.Fill(destinationSentinel, untouched);
            GD.UpdateTexture(
                destination,
                destinationSentinel,
                0,
                0,
                0,
                mipWidth,
                mipHeight,
                1,
                region.MipLevel,
                region.ArrayLayer);
            GD.UpdateTexture(
                destination,
                destinationSentinel,
                0,
                0,
                0,
                mipWidth,
                mipHeight,
                1,
                region.MipLevel,
                arrayLayer: 0);

            byte[] expected = CreateCompressedPattern(
                region.Width,
                region.Height,
                blockSizeInBytes,
                region.PatternSeed);
            expectedRegions[regionIndex] = expected;
            uint subresource = source.CalculateSubresource(
                region.MipLevel,
                region.ArrayLayer);
            MappedResource sourceMap = GD.Map(
                source,
                MapMode.Write,
                subresource);
            try
            {
                FillCompressedMappedMip(
                    sourceMap,
                    mipWidth,
                    mipHeight,
                    blockSizeInBytes,
                    0xC7);
                WriteCompressedMappedRegion(
                    sourceMap,
                    blockSizeInBytes,
                    region,
                    expected);
            }
            finally
            {
                GD.Unmap(source, subresource);
            }
        }
        GD.WaitForIdle();

        CommandList upload = RF.CreateCommandList();
        upload.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 2);
        upload.Begin();
        foreach (CompressedCopyRegion region in regions)
        {
            upload.CopyTexture(
                source,
                region.X,
                region.Y,
                0,
                region.MipLevel,
                region.ArrayLayer,
                destination,
                region.X,
                region.Y,
                0,
                region.MipLevel,
                region.ArrayLayer,
                region.Width,
                region.Height,
                1,
                1);
        }
        upload.End();
        GD.SubmitCommands(upload);
        GD.WaitForIdle();

        Assert.True(upload.TryGetLastSubmissionMetrics(
            out CommandListSubmissionMetrics uploadMetrics));
        Assert.Equal(regions.Length, uploadMetrics.CopyTextureCallCount);

        // Read back whole mip levels at zero offset in a separate submission.
        // The optimal-to-staging direction is independently qualified above,
        // and this deliberately avoids mirroring the upload's region offsets.
        CommandList readback = RF.CreateCommandList();
        readback.Begin();
        foreach (CompressedCopyRegion region in regions)
        {
            Util.GetMipDimensions(
                destination,
                region.MipLevel,
                out uint mipWidth,
                out uint mipHeight,
                out _);
            readback.CopyTexture(
                destination,
                0,
                0,
                0,
                region.MipLevel,
                region.ArrayLayer,
                capture,
                0,
                0,
                0,
                region.MipLevel,
                region.ArrayLayer,
                mipWidth,
                mipHeight,
                1,
                1);
            readback.CopyTexture(
                destination,
                0,
                0,
                0,
                region.MipLevel,
                0,
                capture,
                0,
                0,
                0,
                region.MipLevel,
                0,
                mipWidth,
                mipHeight,
                1,
                1);
        }
        readback.End();
        GD.SubmitCommands(readback);
        GD.WaitForIdle();

        for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
        {
            AssertCompressedMipEqualsDenseRegionAndSentinel(
                capture,
                regions[regionIndex],
                expectedRegions[regionIndex],
                blockSizeInBytes,
                untouched,
                "staging-to-optimal");
            AssertCompressedMipIsSentinel(
                capture,
                regions[regionIndex].MipLevel,
                arrayLayer: 0,
                blockSizeInBytes,
                untouched,
                "staging-to-optimal layer isolation");
        }
    }

    private static void AssertCompressedCopyArguments(
        PixelFormat format,
        uint blockSizeInBytes,
        CompressedCopyRegion[] regions)
    {
        Assert.NotEmpty(regions);
        Assert.Equal(
            blockSizeInBytes,
            FormatHelpers.GetBlockSizeInBytes(format));
    }

    private static void GetAndValidateCompressedRegion(
        Texture texture,
        CompressedCopyRegion region,
        out uint mipWidth,
        out uint mipHeight)
    {
        Assert.True(region.MipLevel < texture.MipLevels);
        Assert.True(region.ArrayLayer < texture.ArrayLayers);
        Assert.Equal(0u, region.X % 4u);
        Assert.Equal(0u, region.Y % 4u);
        Util.GetMipDimensions(
            texture,
            region.MipLevel,
            out mipWidth,
            out mipHeight,
            out _);
        Assert.True(checked(region.X + region.Width) <= mipWidth);
        Assert.True(checked(region.Y + region.Height) <= mipHeight);
        Assert.True(
            region.Width % 4u == 0u || region.X + region.Width == mipWidth);
        Assert.True(
            region.Height % 4u == 0u || region.Y + region.Height == mipHeight);
    }

    private static byte[] CreateCompressedPattern(
        uint width,
        uint height,
        uint blockSizeInBytes,
        byte seed)
    {
        uint byteCount = checked(
            DivideRoundUp(width, 4u) *
            DivideRoundUp(height, 4u) *
            blockSizeInBytes);
        byte[] data = new byte[checked((int)byteCount)];
        for (uint index = 0; index < data.Length; index++)
            data[index] = unchecked((byte)(seed + (index * 37u)));
        return data;
    }

    private static unsafe void WriteCompressedMappedRegion(
        MappedResource mapped,
        uint blockSizeInBytes,
        CompressedCopyRegion region,
        byte[] denseData)
    {
        AssertCompressedMappedRegionFits(
            mapped,
            blockSizeInBytes,
            region.FirstBlockX,
            region.FirstBlockY,
            region.BlockColumns,
            region.BlockRows);
        uint denseRowBytes = checked(
            region.BlockColumns * blockSizeInBytes);
        byte* mappedBase = (byte*)mapped.Data;
        for (uint blockRow = 0; blockRow < region.BlockRows; blockRow++)
        {
            byte* mappedRow = mappedBase + checked((nint)(
                ((nuint)(region.FirstBlockY + blockRow) * mapped.RowPitch) +
                ((nuint)region.FirstBlockX * blockSizeInBytes)));
            denseData.AsSpan(
                checked((int)(blockRow * denseRowBytes)),
                checked((int)denseRowBytes)).CopyTo(
                new Span<byte>(mappedRow, checked((int)denseRowBytes)));
        }
    }

    private static unsafe void FillCompressedMappedMip(
        MappedResource mapped,
        uint width,
        uint height,
        uint blockSizeInBytes,
        byte value)
    {
        uint blockColumns = DivideRoundUp(width, 4u);
        uint blockRows = DivideRoundUp(height, 4u);
        AssertCompressedMappedRegionFits(
            mapped,
            blockSizeInBytes,
            0,
            0,
            blockColumns,
            blockRows);
        int rowSize = checked((int)(blockColumns * blockSizeInBytes));
        byte* mappedBase = (byte*)mapped.Data;
        for (uint blockRow = 0; blockRow < blockRows; blockRow++)
        {
            new Span<byte>(
                mappedBase + checked((nint)((nuint)blockRow * mapped.RowPitch)),
                rowSize).Fill(value);
        }
    }

    private unsafe void AssertCompressedMipEqualsSourceRegionAndSentinel(
        Texture capture,
        CompressedCopyRegion region,
        byte[] sourceMip,
        uint blockSizeInBytes,
        byte untouched,
        string direction)
    {
        Util.GetMipDimensions(
            capture,
            region.MipLevel,
            out uint mipWidth,
            out uint mipHeight,
            out _);
        uint mipBlockColumns = DivideRoundUp(mipWidth, 4u);
        AssertCompressedMappedMip(
            capture,
            region,
            blockSizeInBytes,
            untouched,
            direction,
            (blockX, blockY, byteInBlock) =>
            {
                uint sourceOffset = checked(
                    ((blockY * mipBlockColumns + blockX) * blockSizeInBytes) +
                    byteInBlock);
                return sourceMip[checked((int)sourceOffset)];
            });
    }

    private unsafe void AssertCompressedMipEqualsDenseRegionAndSentinel(
        Texture capture,
        CompressedCopyRegion region,
        byte[] denseRegion,
        uint blockSizeInBytes,
        byte untouched,
        string direction)
    {
        AssertCompressedMappedMip(
            capture,
            region,
            blockSizeInBytes,
            untouched,
            direction,
            (blockX, blockY, byteInBlock) =>
            {
                uint relativeBlockX = blockX - region.FirstBlockX;
                uint relativeBlockY = blockY - region.FirstBlockY;
                uint sourceOffset = checked(
                    ((relativeBlockY * region.BlockColumns + relativeBlockX) *
                        blockSizeInBytes) +
                    byteInBlock);
                return denseRegion[checked((int)sourceOffset)];
            });
    }

    private unsafe void AssertCompressedMipIsSentinel(
        Texture capture,
        uint mipLevel,
        uint arrayLayer,
        uint blockSizeInBytes,
        byte sentinel,
        string direction)
    {
        Util.GetMipDimensions(
            capture,
            mipLevel,
            out uint mipWidth,
            out uint mipHeight,
            out _);
        uint blockColumns = DivideRoundUp(mipWidth, 4u);
        uint blockRows = DivideRoundUp(mipHeight, 4u);
        uint subresource = capture.CalculateSubresource(mipLevel, arrayLayer);
        MappedResource mapped = GD.Map(capture, MapMode.Read, subresource);
        try
        {
            AssertCompressedMappedRegionFits(
                mapped,
                blockSizeInBytes,
                0,
                0,
                blockColumns,
                blockRows);
            byte* mappedBase = (byte*)mapped.Data;
            for (uint blockY = 0; blockY < blockRows; blockY++)
            {
                for (uint blockX = 0; blockX < blockColumns; blockX++)
                {
                    for (uint byteInBlock = 0;
                        byteInBlock < blockSizeInBytes;
                        byteInBlock++)
                    {
                        nuint offset = checked(
                            ((nuint)blockY * mapped.RowPitch) +
                            ((nuint)blockX * blockSizeInBytes) +
                            byteInBlock);
                        byte actual = *(mappedBase + checked((nint)offset));
                        Assert.True(
                            actual == sentinel,
                            $"The {direction} check found 0x{actual:X2} instead of 0x{sentinel:X2} at mip {mipLevel}, layer {arrayLayer}, block ({blockX}, {blockY}), byte {byteInBlock}.");
                    }
                }
            }
        }
        finally
        {
            GD.Unmap(capture, subresource);
        }
    }

    private unsafe void AssertCompressedMappedMip(
        Texture capture,
        CompressedCopyRegion region,
        uint blockSizeInBytes,
        byte untouched,
        string direction,
        Func<uint, uint, uint, byte> getCopiedByte)
    {
        Util.GetMipDimensions(
            capture,
            region.MipLevel,
            out uint mipWidth,
            out uint mipHeight,
            out _);
        uint blockColumns = DivideRoundUp(mipWidth, 4u);
        uint blockRows = DivideRoundUp(mipHeight, 4u);
        uint subresource = capture.CalculateSubresource(
            region.MipLevel,
            region.ArrayLayer);
        MappedResource mapped = GD.Map(capture, MapMode.Read, subresource);
        try
        {
            AssertCompressedMappedRegionFits(
                mapped,
                blockSizeInBytes,
                0,
                0,
                blockColumns,
                blockRows);
            byte* mappedBase = (byte*)mapped.Data;
            for (uint blockY = 0; blockY < blockRows; blockY++)
            {
                for (uint blockX = 0; blockX < blockColumns; blockX++)
                {
                    bool copied =
                        blockX >= region.FirstBlockX &&
                        blockX < region.FirstBlockX + region.BlockColumns &&
                        blockY >= region.FirstBlockY &&
                        blockY < region.FirstBlockY + region.BlockRows;
                    for (uint byteInBlock = 0;
                        byteInBlock < blockSizeInBytes;
                        byteInBlock++)
                    {
                        byte expected = copied
                            ? getCopiedByte(blockX, blockY, byteInBlock)
                            : untouched;
                        nuint offset = checked(
                            ((nuint)blockY * mapped.RowPitch) +
                            ((nuint)blockX * blockSizeInBytes) +
                            byteInBlock);
                        byte actual = *(mappedBase + checked((nint)offset));
                        Assert.True(
                            expected == actual,
                            $"The {direction} copy produced 0x{actual:X2} instead of 0x{expected:X2} at mip {region.MipLevel}, layer {region.ArrayLayer}, block ({blockX}, {blockY}), byte {byteInBlock}.");
                    }
                }
            }
        }
        finally
        {
            GD.Unmap(capture, subresource);
        }
    }

    private static void AssertCompressedMappedRegionFits(
        MappedResource mapped,
        uint blockSizeInBytes,
        uint firstBlockX,
        uint firstBlockY,
        uint blockColumns,
        uint blockRows)
    {
        nuint requiredRowBytes = checked(
            (nuint)(firstBlockX + blockColumns) * blockSizeInBytes);
        Assert.True(requiredRowBytes <= mapped.RowPitch);

        nuint requiredBytes = checked(
            ((nuint)(firstBlockY + blockRows - 1) * mapped.RowPitch) +
            requiredRowBytes);
        Assert.True(requiredBytes <= mapped.SizeInBytes);
    }

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((uint)(((ulong)value + divisor - 1u) / divisor));

    [Fact]
    public void Map_Succeeds()
    {
        Texture texture = RF.CreateTexture(
            TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R32_G32_B32_A32_Float, TextureUsage.Staging));

        MappedResource map = GD.Map(texture, MapMode.ReadWrite, 0);
        GD.Unmap(texture, 0);
    }

    [Fact]
    public void Map_Succeeds_R32_G32_B32_A32_UInt()
    {
        Texture texture = RF.CreateTexture(
            TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R32_G32_B32_A32_UInt, TextureUsage.Staging));

        MappedResource map = GD.Map(texture, MapMode.ReadWrite, 0);
        GD.Unmap(texture, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void Update_ThenMapRead_Succeeds_R32Float(bool useArrayOverload)
    {
        Texture texture = RF.CreateTexture(
            TextureDescription.Texture2D(1024, 1024, 1, 1, PixelFormat.R32_Float, TextureUsage.Staging));

        float[] data = Enumerable.Range(0, 1024 * 1024).Select(i => (float)i).ToArray();

        fixed (float* dataPtr = data)
        {
            if (useArrayOverload)
            {
                GD.UpdateTexture(texture, data, 0, 0, 0, 1024, 1024, 1, 0, 0);
            }
            else
            {
                GD.UpdateTexture(texture, (IntPtr)dataPtr, 1024 * 1024 * 4, 0, 0, 0, 1024, 1024, 1, 0, 0);
            }
        }

        MappedResource map = GD.Map(texture, MapMode.Read, 0);
        float* mappedFloatPtr = (float*)map.Data;

        for (int y = 0; y < 1024; y++)
        {
            for (int x = 0; x < 1024; x++)
            {
                int index = y * 1024 + x;
                Assert.Equal(index, mappedFloatPtr[index]);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void Update_ThenMapRead_SingleMip_Succeeds_R16UNorm(bool useArrayOverload)
    {
        Texture texture = RF.CreateTexture(
            TextureDescription.Texture2D(1024, 1024, 3, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

        ushort[] data = Enumerable.Range(0, 256 * 256).Select(i => (ushort)i).ToArray();

        fixed (ushort* dataPtr = data)
        {
            if (useArrayOverload)
            {
                GD.UpdateTexture(texture, data, 0, 0, 0, 256, 256, 1, 2, 0);
            }
            else
            {
                GD.UpdateTexture(texture, (IntPtr)dataPtr, 256 * 256 * sizeof(ushort), 0, 0, 0, 256, 256, 1, 2, 0);
            }
        }

        MappedResource map = GD.Map(texture, MapMode.Read, 2);
        ushort* mappedUShortPtr = (ushort*)map.Data;

        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                uint mapIndex = (uint)(y * (map.RowPitch / sizeof(ushort)) + x);
                ushort value = (ushort)(y * 256 + x);
                Assert.Equal(value, mappedUShortPtr[mapIndex]);
            }
        }
    }

    [Fact]
    public unsafe void Update_ThenCopySingleMip_Succeeds_R16UNorm()
    {
        TextureDescription desc = TextureDescription.Texture2D(
            1024, 1024, 3, 1, PixelFormat.R16_UNorm, TextureUsage.Staging);
        Texture src = RF.CreateTexture(desc);
        Texture dst = RF.CreateTexture(desc);

        ushort[] data = Enumerable.Range(0, 256 * 256).Select(i => (ushort)i).ToArray();

        fixed (ushort* dataPtr = data)
        {
            GD.UpdateTexture(src, (IntPtr)dataPtr, 256 * 256 * sizeof(ushort), 0, 0, 0, 256, 256, 1, 2, 0);
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(src, dst, 2, 0);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        MappedResource map = GD.Map(dst, MapMode.Read, 2);
        ushort* mappedFloatPtr = (ushort*)map.Data;

        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                uint mapIndex = (uint)(y * (map.RowPitch / sizeof(ushort)) + x);
                ushort value = (ushort)(y * 256 + x);
                Assert.Equal(value, mappedFloatPtr[mapIndex]);
            }
        }
    }


    [Fact]
    public void CreateTextureViewFromTextureWithArrayLayers()
    {
        const uint TexSize = 4;
        const uint MipLevels = 1;
        const uint ArrayLayers = 6;

        TextureDescription texDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, ArrayLayers, PixelFormat.R8_UNorm, TextureUsage.Storage | TextureUsage.Sampled);
        Texture tex = RF.CreateTexture(texDesc);

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            for (uint layer = 0; layer < ArrayLayers; layer++)
            {
                var mipSize = TexSize >> (int)mip;
                byte[] data = Enumerable.Repeat((layer + 1) * 42, (int)(mipSize * mipSize)).Select(n => (byte)n).ToArray();
                GD.UpdateTexture(tex, data, 0, 0, 0, mipSize, mipSize, 1, mip, layer);
            }
        }

        var textureView = RF.CreateTextureView(tex);
        Assert.NotNull(textureView);
    }

    [Fact]
    public void CubeMap_UpdateAndRead()
    {
        const uint TexSize = 4;
        const uint MipLevels = 3;

        TextureDescription texDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 1, PixelFormat.R8_UNorm, TextureUsage.Cubemap);
        Texture tex = RF.CreateTexture(texDesc);

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            for (uint face = 0; face < 6; face++)
            {
                var mipSize = TexSize >> (int)mip;
                byte[] data = Enumerable.Repeat((face + 1) * 42, (int)(mipSize * mipSize)).Select(n => (byte)n).ToArray();
                GD.UpdateTexture(tex, data, 0, 0, 0, mipSize, mipSize, 1, mip, face);
            }
        }

        Texture readback = GetReadback(tex);

        foreach (var mip in Enumerable.Range(0, (int)MipLevels))
        {
            foreach (var face in Enumerable.Range(0, 6))
            {
                var subresource = readback.CalculateSubresource((uint)mip, (uint)face);
                var mipSize = TexSize >> mip;
                byte expectedColor = (byte)((face + 1) * 42);
                var map = GD.Map<byte>(readback, MapMode.Read, subresource);

                foreach (var x in Enumerable.Range(0, (int)mipSize))
                {
                    foreach (var y in Enumerable.Range(0, (int)mipSize))
                    {
                        Assert.Equal(expectedColor, map[x, y]);
                    }
                }

                GD.Unmap(readback, subresource);
            }
        }
    }

    [Fact]
    public void CubeMap_CreateViewWithSingleMipLevel()
    {
        const uint TexSize = 4;
        const uint MipLevels = 3;

        TextureDescription texDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 1, PixelFormat.R8_UNorm, TextureUsage.Cubemap | TextureUsage.Sampled);
        Texture tex = RF.CreateTexture(texDesc);

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            for (uint face = 0; face < 6; face++)
            {
                var mipSize = TexSize >> (int)mip;
                byte[] data = Enumerable.Repeat((face + 1) * 42, (int)(mipSize * mipSize)).Select(n => (byte)n).ToArray();
                GD.UpdateTexture(tex, data, 0, 0, 0, mipSize, mipSize, 1, mip, face);
            }
        }

        var view = RF.CreateTextureView(new TextureViewDescription(tex, 0, 1, 0, 1));
        Assert.NotNull(view);
    }

    [Fact]
    public unsafe void CubeMap_Copy_OneMip()
    {
        const uint TexSize = 64;
        const uint MipLevels = 1;

        TextureDescription srcDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 1, PixelFormat.R8_UNorm, TextureUsage.Cubemap);
        TextureDescription dstDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 6, PixelFormat.R8_UNorm, TextureUsage.Staging);
        Texture src = RF.CreateTexture(srcDesc);
        Texture dst = RF.CreateTexture(dstDesc);

        for (uint face = 0; face < 6; face++)
        {
            byte[] data = Enumerable.Repeat((face + 1) * 42, (int)(TexSize * TexSize)).Select(n => (byte)n).ToArray();
            GD.UpdateTexture(src, data, 0, 0, 0, TexSize, TexSize, 1, 0, face);
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(src, dst);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        foreach (var mip in Enumerable.Range(0, (int)MipLevels))
        {
            foreach (var face in Enumerable.Range(0, 6))
            {
                var subresource = dst.CalculateSubresource((uint)mip, (uint)face);
                var mipSize = (uint)(TexSize / (1 << mip));
                byte expectedColor = (byte)((face + 1) * 42);
                var map = GD.Map<byte>(dst, MapMode.Read, subresource);

                foreach (var x in Enumerable.Range(0, (int)mipSize))
                {
                    foreach (var y in Enumerable.Range(0, (int)mipSize))
                    {
                        Assert.Equal(expectedColor, map[x, y]);
                    }
                }

                GD.Unmap(dst, subresource);
            }
        }
    }

    [Fact]
    public unsafe void CubeMap_Copy_FromNonCubeMapWith6ArrayLayers()
    {
        const uint TexSize = 64;
        const uint MipLevels = 1;

        TextureDescription srcDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 6, PixelFormat.R8_UNorm, TextureUsage.Staging);
        TextureDescription dstDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 1, PixelFormat.R8_UNorm, TextureUsage.Sampled | TextureUsage.Cubemap);
        Texture src = RF.CreateTexture(srcDesc);
        Texture dst = RF.CreateTexture(dstDesc);

        for (uint face = 0; face < 6; face++)
        {
            byte[] data = Enumerable.Repeat((face + 1) * 42, (int)(TexSize * TexSize)).Select(n => (byte)n).ToArray();
            GD.UpdateTexture(src, data, 0, 0, 0, TexSize, TexSize, 1, 0, face);
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        for (uint face = 0; face < 6; face++)
            cl.CopyTexture(src, dst, 0, face);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        var readback = GetReadback(dst);

        foreach (var mip in Enumerable.Range(0, (int)MipLevels))
        {
            foreach (var face in Enumerable.Range(0, 6))
            {
                var subresource = readback.CalculateSubresource((uint)mip, (uint)face);
                var mipSize = (uint)(TexSize / (1 << mip));
                byte expectedColor = (byte)((face + 1) * 42);
                var map = GD.Map<byte>(readback, MapMode.Read, subresource);

                foreach (var x in Enumerable.Range(0, (int)mipSize))
                {
                    foreach (var y in Enumerable.Range(0, (int)mipSize))
                    {
                        Assert.Equal(expectedColor, map[x, y]);
                    }
                }

                GD.Unmap(readback, subresource);
            }
        }
    }

    [Fact]
    public void CubeMap_Copy_MultipleMip_CopySingleMipFaces()
    {
        const uint TexSize = 64;
        const uint MipLevels = 3;
        const uint CopiedMip = 1;

        TextureDescription srcDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 1, PixelFormat.R8_UNorm, TextureUsage.Cubemap);
        TextureDescription dstDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 6, PixelFormat.R8_UNorm, TextureUsage.Staging);
        Texture src = RF.CreateTexture(srcDesc);
        Texture dst = RF.CreateTexture(dstDesc);

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            var mipSize = (uint)(TexSize / (1 << (int)mip));
            for (uint face = 0; face < 6; face++)
            {
                byte[] data = Enumerable.Repeat((face + 1) * 42, (int)(mipSize * mipSize)).Select(n => (byte)n).ToArray();
                GD.UpdateTexture(src, data, 0, 0, 0, mipSize, mipSize, 1, mip, face);
            }
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        for (uint face = 0; face < 6; face++)
            cl.CopyTexture(src, dst, CopiedMip, face);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            for (uint face = 0; face < 6; face++)
            {
                var subresource = dst.CalculateSubresource(mip, face);
                var mipSize = (uint)(TexSize / (1 << (int)mip));
                byte expectedColor = mip == CopiedMip ? (byte)((face + 1) * 42) : (byte)0;
                var map = GD.Map<byte>(dst, MapMode.Read, subresource);
                for (int y = 0; y < mipSize; y++)
                    for (int x = 0; x < mipSize; x++)
                    {
                        Assert.Equal(expectedColor, map[x, y]);
                    }
                GD.Unmap(dst, subresource);
            }
        }
    }

    [Fact]
    public void CubeMap_Copy_MultipleMip_AllAtOnce()
    {
        const uint TexSize = 64;
        const uint MipLevels = 2;

        TextureDescription srcDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 1, PixelFormat.R8_UNorm, TextureUsage.Cubemap);
        TextureDescription dstDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 6, PixelFormat.R8_UNorm, TextureUsage.Staging);
        Texture src = RF.CreateTexture(srcDesc);
        Texture dst = RF.CreateTexture(dstDesc);

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            var mipSize = (uint)(TexSize / (1 << (int)mip));
            for (uint face = 0; face < 6; face++)
            {
                byte[] data = Enumerable.Repeat((face + 1) * 42, (int)(mipSize * mipSize)).Select(n => (byte)n).ToArray();
                GD.UpdateTexture(src, data, 0, 0, 0, mipSize, mipSize, 1, mip, face);
            }
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(src, dst);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        foreach (var mip in Enumerable.Range(0, (int)MipLevels))
        {
            foreach (var face in Enumerable.Range(0, 6))
            {
                var subresource = dst.CalculateSubresource((uint)mip, (uint)face);
                var mipSize = (uint)(TexSize / (1 << mip));
                byte expectedColor = (byte)((face + 1) * 42);
                var map = GD.Map<byte>(dst, MapMode.Read, subresource);

                foreach (var x in Enumerable.Range(0, (int)mipSize))
                {
                    foreach (var y in Enumerable.Range(0, (int)mipSize))
                    {
                        Assert.Equal(expectedColor, map[x, y]);
                    }
                }

                GD.Unmap(dst, subresource);
            }
        }
    }

    [Fact]
    public void CubeMap_Copy_MultipleMip_SpecificArrayLayer()
    {
        const uint TexSize = 64;
        const uint MipLevels = 2;
        const uint CopiedArrayLayer = 3;

        TextureDescription srcDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 1, PixelFormat.R8_UNorm, TextureUsage.Cubemap);
        TextureDescription dstDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, CopiedArrayLayer + 1, PixelFormat.R8_UNorm, TextureUsage.Staging);
        Texture src = RF.CreateTexture(srcDesc);
        Texture dst = RF.CreateTexture(dstDesc);

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            var mipSize = (uint)(TexSize / (1 << (int)mip));
            for (uint face = 0; face < 6; face++)
            {
                byte[] data = Enumerable.Repeat((face + 1) * 42, (int)(mipSize * mipSize)).Select(n => (byte)n).ToArray();
                GD.UpdateTexture(src, data, 0, 0, 0, mipSize, mipSize, 1, mip, face);
            }
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        for (uint mip = 0; mip < MipLevels; mip++)
            cl.CopyTexture(src, dst, mip, CopiedArrayLayer);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            for (uint face = 0; face <= CopiedArrayLayer; face++)
            {
                var subresource = dst.CalculateSubresource(mip, face);
                var mipSize = (uint)(TexSize / (1 << (int)mip));
                byte expectedColor = face == CopiedArrayLayer ? (byte)((face + 1) * 42) : (byte)0;
                var map = GD.Map<byte>(dst, MapMode.Read, subresource);
                for (int y = 0; y < mipSize; y++)
                    for (int x = 0; x < mipSize; x++)
                    {
                        Assert.Equal(expectedColor, map[x, y]);
                    }
                GD.Unmap(dst, subresource);
            }
        }
    }

    [Theory]
    [InlineData(64, 7)]
    [InlineData(64, 4)]
    [InlineData(64, 2)]
    [InlineData(32, 6)]
    [InlineData(32, 4)]
    [InlineData(32, 2)]
    [InlineData(4, 3)]
    [InlineData(4, 2)]
    [InlineData(2, 2)]
    public void CubeMap_GenerateMipmaps(uint TexSize, uint MipLevels)
    {
        TextureDescription texDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, 1, PixelFormat.R8_UNorm, TextureUsage.Cubemap | TextureUsage.GenerateMipmaps);
        Texture tex = RF.CreateTexture(texDesc);

        for (uint face = 0; face < 6; face++)
        {
            byte[] data = Enumerable.Repeat((face + 1) * 42, (int)(TexSize * TexSize)).Select(n => (byte)n).ToArray();
            GD.UpdateTexture(tex, data, 0, 0, 0, TexSize, TexSize, 1, 0, face);
        }

        Texture readback = GetReadback(tex);
        foreach (var face in Enumerable.Range(0, 6))
        {
            var subresource = readback.CalculateSubresource(0, (uint)face);
            var mipSize = TexSize;
            byte expectedColor = (byte)((face + 1) * 42);
            var map = GD.Map<byte>(readback, MapMode.Read, subresource);

            foreach (var x in Enumerable.Range(0, (int)mipSize))
            {
                foreach (var y in Enumerable.Range(0, (int)mipSize))
                {
                    Assert.Equal(expectedColor, map[x, y]);
                }
            }

            GD.Unmap(readback, subresource);
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.GenerateMipmaps(tex);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        readback = GetReadback(tex);
        foreach (var mip in Enumerable.Range(0, (int)MipLevels))
        {
            foreach (var face in Enumerable.Range(0, 6))
            {
                var subresource = readback.CalculateSubresource((uint)mip, (uint)face);
                var mipSize = (uint)(TexSize / (1 << mip));
                byte expectedColor = (byte)((face + 1) * 42);
                var map = GD.Map<byte>(readback, MapMode.Read, subresource);

                foreach (var x in Enumerable.Range(0, (int)mipSize))
                {
                    foreach (var y in Enumerable.Range(0, (int)mipSize))
                    {
                        Assert.Equal(expectedColor, map[x, y]);
                    }
                }

                GD.Unmap(readback, subresource);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void ArrayLayers_StagingWriteAndRead_SmallTextures(uint TexSize)
    {
        const uint ArrayLayers = 6;
        const uint ArrayColorDelta = 255 / ArrayLayers;

        TextureDescription texDesc = TextureDescription.Texture2D(
            TexSize, TexSize, 1, ArrayLayers, PixelFormat.R8_UNorm, TextureUsage.Staging);
        Texture tex = RF.CreateTexture(texDesc);

        for (uint layer = 0; layer < ArrayLayers; layer++)
        {
            byte[] data = Enumerable.Repeat(layer * ArrayColorDelta, (int)(TexSize * TexSize)).Select(n => (byte)n).ToArray();
            GD.UpdateTexture(tex, data, 0, 0, 0, TexSize, TexSize, 1, 0, layer);
        }

        for (uint layer = 0; layer < ArrayLayers; layer++)
        {
            var subresource = tex.CalculateSubresource(0, layer);
            byte expectedColor = (byte)(layer * ArrayColorDelta);
            var map = GD.Map<byte>(tex, MapMode.Read, subresource);
            for (int y = 0; y < TexSize; y++)
                for (int x = 0; x < TexSize; x++)
                {
                    Assert.Equal(expectedColor, map[x, y]);
                }
            GD.Unmap(tex, subresource);
        }
    }

    [Fact]
    public void ArrayLayers_StagingWriteAndRead()
    {
        const uint TexSize = 64;
        const uint ArrayLayers = 6;
        const uint ArrayColorDelta = 255 / ArrayLayers;

        TextureDescription texDesc = TextureDescription.Texture2D(
            TexSize, TexSize, 1, ArrayLayers, PixelFormat.R8_UNorm, TextureUsage.Staging);
        Texture tex = RF.CreateTexture(texDesc);

        for (uint layer = 0; layer < ArrayLayers; layer++)
        {
            byte[] data = Enumerable.Repeat(layer * ArrayColorDelta, (int)(TexSize * TexSize)).Select(n => (byte)n).ToArray();
            GD.UpdateTexture(tex, data, 0, 0, 0, TexSize, TexSize, 1, 0, layer);
        }

        for (uint layer = 0; layer < ArrayLayers; layer++)
        {
            var subresource = tex.CalculateSubresource(0, layer);
            byte expectedColor = (byte)(layer * ArrayColorDelta);
            var map = GD.Map<byte>(tex, MapMode.Read, subresource);
            for (int y = 0; y < TexSize; y++)
                for (int x = 0; x < TexSize; x++)
                {
                    Assert.Equal(expectedColor, map[x, y]);
                }
            GD.Unmap(tex, subresource);
        }
    }

    [Fact]
    public void ArrayLayers_WriteAndCopyAndRead()
    {
        const uint TexSize = 64;
        const uint MipLevels = 2;
        const uint ArrayLayers = 6;
        const uint ArrayColorDelta = 255 / ArrayLayers;

        TextureDescription texDesc = TextureDescription.Texture2D(
            TexSize, TexSize, MipLevels, ArrayLayers, PixelFormat.R8_UNorm, TextureUsage.Sampled);
        Texture tex = RF.CreateTexture(texDesc);
        texDesc.Usage = TextureUsage.Staging;
        Texture readback = RF.CreateTexture(texDesc);

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            for (uint layer = 0; layer < ArrayLayers; layer++)
            {
                var mipSize = MipLevels >> (int)mip;
                byte[] data = Enumerable.Repeat(layer * ArrayColorDelta, (int)(mipSize * mipSize)).Select(n => (byte)n).ToArray();
                GD.UpdateTexture(tex, data, 0, 0, 0, mipSize, mipSize, 1, mip, layer);
            }
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(tex, readback);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        for (uint mip = 0; mip < MipLevels; mip++)
        {
            for (uint layer = 0; layer < ArrayLayers; layer++)
            {
                var mipSize = MipLevels >> (int)mip;
                var subresource = readback.CalculateSubresource(0, layer);
                byte expectedColor = (byte)(layer * ArrayColorDelta);
                var map = GD.Map<byte>(readback, MapMode.Read, subresource);
                for (int y = 0; y < mipSize; y++)
                    for (int x = 0; x < mipSize; x++)
                    {
                        Assert.Equal(expectedColor, map[x, y]);
                    }
                GD.Unmap(readback, subresource);
            }
        }
    }

    [SkippableTheory]
    [InlineData(PixelFormat.BC1_Rgb_UNorm, 8, 0, 0, 64, 64)]
    [InlineData(PixelFormat.BC1_Rgb_UNorm, 8, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC1_Rgb_UNorm_SRgb, 8, 0, 0, 64, 64)]
    [InlineData(PixelFormat.BC1_Rgb_UNorm_SRgb, 8, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 8, 0, 0, 64, 64)]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 8, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC1_Rgba_UNorm_SRgb, 8, 0, 0, 64, 64)]
    [InlineData(PixelFormat.BC1_Rgba_UNorm_SRgb, 8, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC2_UNorm, 16, 0, 0, 64, 64)]
    [InlineData(PixelFormat.BC2_UNorm, 16, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC2_UNorm_SRgb, 16, 0, 0, 64, 64)]
    [InlineData(PixelFormat.BC2_UNorm_SRgb, 16, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC3_UNorm, 16, 0, 0, 64, 64)]
    [InlineData(PixelFormat.BC3_UNorm, 16, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC3_UNorm_SRgb, 16, 0, 0, 64, 64)]
    [InlineData(PixelFormat.BC3_UNorm_SRgb, 16, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC4_UNorm, 8, 0, 0, 16, 16)]
    [InlineData(PixelFormat.BC4_UNorm, 8, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC4_SNorm, 8, 0, 0, 16, 16)]
    [InlineData(PixelFormat.BC4_SNorm, 8, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC5_UNorm, 16, 0, 0, 16, 16)]
    [InlineData(PixelFormat.BC5_UNorm, 16, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC5_SNorm, 16, 0, 0, 16, 16)]
    [InlineData(PixelFormat.BC5_SNorm, 16, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC7_UNorm, 16, 0, 0, 16, 16)]
    [InlineData(PixelFormat.BC7_UNorm, 16, 8, 4, 16, 16)]
    [InlineData(PixelFormat.BC7_UNorm_SRgb, 16, 0, 0, 16, 16)]
    [InlineData(PixelFormat.BC7_UNorm_SRgb, 16, 8, 4, 16, 16)]
    public unsafe void Copy_Compressed_Texture(PixelFormat format, uint blockSizeInBytes, uint srcX, uint srcY, uint copyWidth, uint copyHeight)
    {
        Skip.IfNot(
            GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Sampled)
                && GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Staging),
            $"NV-SKIP-COMPRESSED-STAGING: {format} compressed staging readback is unavailable on {GD.BackendType}.");

        Texture copySrc = RF.CreateTexture(TextureDescription.Texture2D(
            64, 64, 1, 1, format, TextureUsage.Sampled));
        Texture copyDst = RF.CreateTexture(TextureDescription.Texture2D(
            copyWidth, copyHeight, 1, 1, format, TextureUsage.Staging));

        const int numPixelsInBlockSide = 4;
        const int numPixelsInBlock = 16;

        uint totalDataSize = copyWidth * copyHeight / numPixelsInBlock * blockSizeInBytes;
        byte[] data = new byte[totalDataSize];

        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }
        fixed (byte* dataPtr = data)
        {
            GD.UpdateTexture(copySrc, (IntPtr)dataPtr, totalDataSize, srcX, srcY, 0, copyWidth, copyHeight, 1, 0, 0);
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(
            copySrc, srcX, srcY, 0, 0, 0,
            copyDst, 0, 0, 0, 0, 0,
            copyWidth, copyHeight, 1, 1);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        uint numBytesPerRow = copyWidth / numPixelsInBlockSide * blockSizeInBytes;
        MappedResourceView<byte> view = GD.Map<byte>(copyDst, MapMode.Read);
        for (uint i = 0; i < data.Length; i++)
        {
            uint viewRow = i / numBytesPerRow;
            uint viewIndex = (view.MappedResource.RowPitch * viewRow) + (i % numBytesPerRow);
            Assert.Equal(data[i], view[viewIndex]);
        }
        GD.Unmap(copyDst);
    }

    [InlineData(true)]
    [InlineData(false)]
    [SkippableTheory]
    public unsafe void Copy_Compressed_Array(bool separateLayerCopies)
    {
        PixelFormat format = PixelFormat.BC3_UNorm;
        Skip.IfNot(
            GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Sampled),
            $"NV-SKIP-COMPRESSED-SAMPLING: {format} sampling is unavailable on {GD.BackendType}.");

        bool supportsCompressedStaging = GD.GetPixelFormatSupport(
            format,
            TextureType.Texture2D,
            TextureUsage.Staging);

        // OpenGL ES has no core API for downloading raw compressed texture blocks. When
        // compressed staging is unavailable, reinterpret each 128-bit BC3 block as one
        // R32_G32_B32_A32_UInt texel with CopyImageSubData, then map that uncompressed
        // texture through the normal staging path.
        const PixelFormat rawBlockFormat = PixelFormat.R32_G32_B32_A32_UInt;
        bool hasCoreCopyImage = false;
#if TEST_OPENGLES
        hasCoreCopyImage = GD.BackendType == GraphicsBackend.OpenGLES
            && GraphicsApiVersion.TryParseGLVersion(GD.GetOpenGLInfo().Version, out GraphicsApiVersion apiVersion)
            && (apiVersion.Major > 3 || apiVersion.Major == 3 && apiVersion.Minor >= 2);
#endif
        bool useCompatibleRawReadback = !supportsCompressedStaging
            && hasCoreCopyImage
            && GD.GetPixelFormatSupport(rawBlockFormat, TextureType.Texture2D, TextureUsage.Staging);
        Skip.IfNot(
            supportsCompressedStaging || useCompatibleRawReadback,
            $"NV-SKIP-COMPRESSED-COPY-READBACK: Exact compressed copy readback is unavailable on {GD.BackendType}.");

        TextureDescription texDesc = TextureDescription.Texture2D(
            16, 16,
            1, 4,
            format,
            TextureUsage.Sampled);

        Texture copySrc = RF.CreateTexture(texDesc);
        texDesc.Usage = supportsCompressedStaging ? TextureUsage.Staging : TextureUsage.Sampled;
        Texture copyDst = RF.CreateTexture(texDesc);
        Texture readback = useCompatibleRawReadback
            ? RF.CreateTexture(TextureDescription.Texture2D(
                16, 16,
                1, copySrc.ArrayLayers,
                rawBlockFormat,
                TextureUsage.Staging))
            : copyDst;

        for (uint layer = 0; layer < copySrc.ArrayLayers; layer++)
        {
            int byteCount = 16 * 16;
            byte[] data = Enumerable.Range(0, byteCount).Select(i => (byte)(i + layer)).ToArray();
            GD.UpdateTexture(
                copySrc,
                data,
                0, 0, 0,
                16, 16, 1,
                0, layer);
        }

        CommandList copyCL = RF.CreateCommandList();
        copyCL.Begin();
        if (separateLayerCopies)
        {
            for (uint layer = 0; layer < copySrc.ArrayLayers; layer++)
            {
                copyCL.CopyTexture(copySrc, 0, 0, 0, 0, layer, copyDst, 0, 0, 0, 0, layer, 16, 16, 1, 1);
            }
        }
        else
        {
            copyCL.CopyTexture(copySrc, 0, 0, 0, 0, 0, copyDst, 0, 0, 0, 0, 0, 16, 16, 1, copySrc.ArrayLayers);
        }

        if (useCompatibleRawReadback)
        {
            // Keep the verification copy single-layer so the theory parameter continues to
            // isolate the behavior of the compressed-to-compressed copy above.
            for (uint layer = 0; layer < copyDst.ArrayLayers; layer++)
            {
                copyCL.CopyTexture(copyDst, 0, 0, 0, 0, layer, readback, 0, 0, 0, 0, layer, 16, 16, 1, 1);
            }
        }
        copyCL.End();
        Fence fence = RF.CreateFence(false);
        GD.SubmitCommands(copyCL, fence);
        GD.WaitForFence(fence);

        for (uint layer = 0; layer < readback.ArrayLayers; layer++)
        {
            MappedResource map = GD.Map(readback, MapMode.Read, layer);
            byte* basePtr = (byte*)map.Data;

            int index = 0;
            uint rowSize = 64;
            uint numRows = 4;
            for (uint row = 0; row < numRows; row++)
            {
                byte* rowBase = basePtr + (row * map.RowPitch);
                for (uint x = 0; x < rowSize; x++)
                {
                    Assert.Equal((byte)(index + layer), rowBase[x]);
                    index += 1;
                }
            }

            GD.Unmap(readback, layer);
        }
    }

    [Fact]
    public unsafe void Update_ThenMapRead_3D()
    {
        Texture tex3D = RF.CreateTexture(TextureDescription.Texture3D(
            10, 10, 10, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

        RgbaByte[] data = new RgbaByte[tex3D.Width * tex3D.Height * tex3D.Depth];
        for (int z = 0; z < tex3D.Depth; z++)
            for (int y = 0; y < tex3D.Height; y++)
                for (int x = 0; x < tex3D.Width; x++)
                {
                    int index = (int)(z * tex3D.Width * tex3D.Height + y * tex3D.Height + x);
                    data[index] = new RgbaByte((byte)x, (byte)y, (byte)z, 1);
                }

        fixed (RgbaByte* dataPtr = data)
        {
            GD.UpdateTexture(tex3D, (IntPtr)dataPtr, (uint)(data.Length * Unsafe.SizeOf<RgbaByte>()),
                0, 0, 0,
                tex3D.Width, tex3D.Height, tex3D.Depth,
                0, 0);
        }

        MappedResourceView<RgbaByte> view = GD.Map<RgbaByte>(tex3D, MapMode.Read, 0);
        for (int z = 0; z < tex3D.Depth; z++)
            for (int y = 0; y < tex3D.Height; y++)
                for (int x = 0; x < tex3D.Width; x++)
                {
                    Assert.Equal(new RgbaByte((byte)x, (byte)y, (byte)z, 1), view[x, y, z]);
                }
        GD.Unmap(tex3D);
    }

    [Fact]
    public unsafe void MapWrite_ThenMapRead_3D()
    {
        Texture tex3D = RF.CreateTexture(TextureDescription.Texture3D(
            10, 10, 10, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

        MappedResourceView<RgbaByte> writeView = GD.Map<RgbaByte>(tex3D, MapMode.Write);
        for (int z = 0; z < tex3D.Depth; z++)
            for (int y = 0; y < tex3D.Height; y++)
                for (int x = 0; x < tex3D.Width; x++)
                {
                    writeView[x, y, z] = new RgbaByte((byte)x, (byte)y, (byte)z, 1);
                }
        GD.Unmap(tex3D);

        MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(tex3D, MapMode.Read, 0);
        for (int z = 0; z < tex3D.Depth; z++)
            for (int y = 0; y < tex3D.Height; y++)
                for (int x = 0; x < tex3D.Width; x++)
                {
                    Assert.Equal(new RgbaByte((byte)x, (byte)y, (byte)z, 1), readView[x, y, z]);
                }
        GD.Unmap(tex3D);
    }

    [SkippableFact]
    public unsafe void Update_ThenMapRead_1D()
    {
        Skip.IfNot(
            GD.Features.Texture1D,
            $"NV-SKIP-TEXTURE1D: One-dimensional textures are unavailable on {GD.BackendType}.");

        Texture tex1D = RF.CreateTexture(
            TextureDescription.Texture1D(100, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));
        ushort[] data = Enumerable.Range(0, (int)tex1D.Width).Select(i => (ushort)(i * 2)).ToArray();
        fixed (ushort* dataPtr = &data[0])
        {
            GD.UpdateTexture(tex1D, (IntPtr)dataPtr, (uint)(data.Length * sizeof(ushort)), 0, 0, 0, tex1D.Width, 1, 1, 0, 0);
        }

        MappedResourceView<ushort> view = GD.Map<ushort>(tex1D, MapMode.Read);
        for (int i = 0; i < tex1D.Width; i++)
        {
            Assert.Equal((ushort)(i * 2), view[i]);
        }
        GD.Unmap(tex1D);
    }

    [SkippableFact]
    public unsafe void MapWrite_ThenMapRead_1D()
    {
        Skip.IfNot(
            GD.Features.Texture1D,
            $"NV-SKIP-TEXTURE1D: One-dimensional textures are unavailable on {GD.BackendType}.");

        Texture tex1D = RF.CreateTexture(
            TextureDescription.Texture1D(100, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

        MappedResourceView<ushort> writeView = GD.Map<ushort>(tex1D, MapMode.Write);
        for (int i = 0; i < tex1D.Width; i++)
        {
            writeView[i] = (ushort)(i * 2);
        }
        GD.Unmap(tex1D);

        MappedResourceView<ushort> view = GD.Map<ushort>(tex1D, MapMode.Read);
        for (int i = 0; i < tex1D.Width; i++)
        {
            Assert.Equal((ushort)(i * 2), view[i]);
        }
        GD.Unmap(tex1D);
    }

    [SkippableFact]
    public unsafe void Copy_1DTo2D()
    {
        Skip.IfNot(
            GD.Features.Texture1D,
            $"NV-SKIP-TEXTURE1D: One-dimensional textures are unavailable on {GD.BackendType}.");

        Texture tex1D = RF.CreateTexture(
            TextureDescription.Texture1D(100, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));
        Texture tex2D = RF.CreateTexture(
            TextureDescription.Texture2D(100, 10, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

        MappedResourceView<ushort> writeView = GD.Map<ushort>(tex1D, MapMode.Write);
        for (int i = 0; i < tex1D.Width; i++)
        {
            writeView[i] = (ushort)(i * 2);
        }
        GD.Unmap(tex1D);

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(
            tex1D, 0, 0, 0, 0, 0,
            tex2D, 0, 5, 0, 0, 0,
            tex1D.Width, 1, 1, 1);
        cl.End();
        GD.SubmitCommands(cl);
        cl.Dispose();
        GD.WaitForIdle();

        MappedResourceView<ushort> readView = GD.Map<ushort>(tex2D, MapMode.Read);
        for (int i = 0; i < tex2D.Width; i++)
        {
            Assert.Equal((ushort)(i * 2), readView[i, 5]);
        }
        GD.Unmap(tex2D);
    }

    [SkippableFact]
    public void Update_MultipleMips_1D()
    {
        Skip.IfNot(
            GD.Features.Texture1D,
            $"NV-SKIP-TEXTURE1D: One-dimensional textures are unavailable on {GD.BackendType}.");

        Texture tex1D = RF.CreateTexture(TextureDescription.Texture1D(
            100, 5, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

        for (uint level = 0; level < tex1D.MipLevels; level++)
        {
            MappedResourceView<RgbaByte> writeView = GD.Map<RgbaByte>(tex1D, MapMode.Write, level);
            for (int i = 0; i < writeView.Count; i++)
            {
                writeView[i] = new RgbaByte((byte)i, (byte)(i * 2), (byte)level, 1);
            }
            GD.Unmap(tex1D, level);
        }

        for (uint level = 0; level < tex1D.MipLevels; level++)
        {
            MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(tex1D, MapMode.Read, level);
            for (int i = 0; i < readView.Count; i++)
            {
                Assert.Equal(new RgbaByte((byte)i, (byte)(i * 2), (byte)level, 1), readView[i]);
            }
            GD.Unmap(tex1D, level);
        }
    }

    [SkippableFact]
    public void Copy_DifferentMip_1DTo2D()
    {
        Skip.IfNot(
            GD.Features.Texture1D,
            $"NV-SKIP-TEXTURE1D: One-dimensional textures are unavailable on {GD.BackendType}.");

        Texture tex1D = RF.CreateTexture(
            TextureDescription.Texture1D(200, 2, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));
        Texture tex2D = RF.CreateTexture(
            TextureDescription.Texture2D(100, 10, 1, 1, PixelFormat.R16_UNorm, TextureUsage.Staging));

        MappedResourceView<ushort> writeView = GD.Map<ushort>(tex1D, MapMode.Write, 1);
        for (int i = 0; i < tex2D.Width; i++)
        {
            writeView[i] = (ushort)(i * 2);
        }
        GD.Unmap(tex1D, 1);

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(
            tex1D, 0, 0, 0, 1, 0,
            tex2D, 0, 5, 0, 0, 0,
            tex2D.Width, 1, 1, 1);
        cl.End();
        GD.SubmitCommands(cl);
        cl.Dispose();
        GD.WaitForIdle();

        MappedResourceView<ushort> readView = GD.Map<ushort>(tex2D, MapMode.Read);
        for (int i = 0; i < tex2D.Width; i++)
        {
            Assert.Equal((ushort)(i * 2), readView[i, 5]);
        }
        GD.Unmap(tex2D);
    }

    [InlineData(TextureUsage.Staging, TextureUsage.Staging)]
    [InlineData(TextureUsage.Staging, TextureUsage.Sampled)]
    [InlineData(TextureUsage.Sampled, TextureUsage.Staging)]
    [InlineData(TextureUsage.Sampled, TextureUsage.Sampled)]
    [Theory]
    public void Copy_WithOffsets_2D(TextureUsage srcUsage, TextureUsage dstUsage)
    {
        Texture src = RF.CreateTexture(TextureDescription.Texture2D(
            100, 100, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, srcUsage));

        Texture dst = RF.CreateTexture(TextureDescription.Texture2D(
            100, 100, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, dstUsage));

        RgbaByte[] srcData = new RgbaByte[src.Height * src.Width];
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                srcData[y * src.Width + x] = new RgbaByte((byte)x, (byte)y, 0, 1);
            }

        GD.UpdateTexture(src, srcData, 0, 0, 0, src.Width, src.Height, 1, 0, 0);

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(
            src,
            50, 50, 0, 0, 0,
            dst,
            10, 10, 0, 0, 0,
            50, 50, 1, 1);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        Texture readback = GetReadback(dst);
        MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(readback, MapMode.Read);
        for (int y = 10; y < 60; y++)
            for (int x = 10; x < 60; x++)
            {
                Assert.Equal(new RgbaByte((byte)(x + 40), (byte)(y + 40), 0, 1), readView[x, y]);
            }
        GD.Unmap(readback);
    }

    [Fact]
    public void Copy_ArrayToNonArray()
    {
        Texture src = RF.CreateTexture(TextureDescription.Texture2D(
            10, 10, 1, 10, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));
        Texture dst = RF.CreateTexture(TextureDescription.Texture2D(
            10, 10, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

        MappedResourceView<RgbaByte> writeView = GD.Map<RgbaByte>(src, MapMode.Write, 5);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                writeView[x, y] = new RgbaByte((byte)x, (byte)y, 0, 1);
            }
        GD.Unmap(src, 5);

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(
            src, 0, 0, 0, 0, 5,
            dst, 0, 0, 0, 0, 0,
            10, 10, 1, 1);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(dst, MapMode.Read);
        for (int y = 0; y < dst.Height; y++)
            for (int x = 0; x < dst.Width; x++)
            {
                Assert.Equal(new RgbaByte((byte)x, (byte)y, 0, 1), readView[x, y]);
            }
        GD.Unmap(dst);
    }

    [Fact]
    public void Map_ThenRead_MultipleArrayLayers()
    {
        Texture src = RF.CreateTexture(TextureDescription.Texture2D(
            10, 10, 1, 10, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

        for (uint layer = 0; layer < src.ArrayLayers; layer++)
        {
            MappedResourceView<RgbaByte> writeView = GD.Map<RgbaByte>(src, MapMode.Write, layer);
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                {
                    writeView[x, y] = new RgbaByte((byte)x, (byte)y, (byte)layer, 1);
                }
            GD.Unmap(src, layer);
        }

        for (uint layer = 0; layer < src.ArrayLayers; layer++)
        {
            MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(src, MapMode.Read, layer);
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                {
                    Assert.Equal(new RgbaByte((byte)x, (byte)y, (byte)layer, 1), readView[x, y]);
                }
            GD.Unmap(src, layer);
        }
    }

    [Fact]
    public unsafe void Update_WithOffset_2D()
    {
        Texture tex2D = RF.CreateTexture(TextureDescription.Texture2D(
            100, 100, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

        RgbaByte[] data = new RgbaByte[50 * 30];
        for (uint y = 0; y < 30; y++)
            for (uint x = 0; x < 50; x++)
            {
                data[y * 50 + x] = new RgbaByte((byte)x, (byte)y, 0, 1);
            }

        fixed (RgbaByte* dataPtr = &data[0])
        {
            GD.UpdateTexture(
                tex2D, (IntPtr)dataPtr, (uint)(data.Length * sizeof(RgbaByte)),
                50, 70, 0,
                50, 30, 1,
                0, 0);
        }

        MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(tex2D, MapMode.Read);
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 50; x++)
            {
                Assert.Equal(new RgbaByte((byte)x, (byte)y, 0, 1), readView[x + 50, y + 70]);
            }
    }

    [Fact]
    public unsafe void Update_NonMultipleOfFourWithCompressedTexture_2D()
    {
        Texture tex2D = RF.CreateTexture(TextureDescription.Texture2D(
            2, 2, 1, 1, PixelFormat.BC1_Rgb_UNorm, TextureUsage.Sampled));

        byte[] data = new byte[16];

        fixed (byte* dataPtr = &data[0])
        {
            GD.UpdateTexture(
                tex2D, (IntPtr)dataPtr, (uint)data.Length,
                0, 0, 0,
                4, 4, 1,
                0, 0);
        }
    }

    [Fact]
    public unsafe void Map_NonZeroMip_3D()
    {
        Texture tex3D = RF.CreateTexture(TextureDescription.Texture3D(
            40, 40, 40, 3, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

        MappedResourceView<RgbaByte> writeView = GD.Map<RgbaByte>(tex3D, MapMode.Write, 2);
        for (int z = 0; z < 10; z++)
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                {
                    writeView[x, y, z] = new RgbaByte((byte)x, (byte)y, (byte)z, 1);
                }
        GD.Unmap(tex3D, 2);

        MappedResourceView<RgbaByte> readView = GD.Map<RgbaByte>(tex3D, MapMode.Read, 2);
        for (int z = 0; z < 10; z++)
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                {
                    Assert.Equal(new RgbaByte((byte)x, (byte)y, (byte)z, 1), readView[x, y, z]);
                }
        GD.Unmap(tex3D, 2);
    }

    [Fact]
    public unsafe void Update_NonStaging_3D()
    {
        Texture tex3D = RF.CreateTexture(TextureDescription.Texture3D(
            16, 16, 16, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
        RgbaByte[] data = new RgbaByte[16 * 16 * 16];
        for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    int index = (int)(z * tex3D.Width * tex3D.Height + y * tex3D.Height + x);
                    data[index] = new RgbaByte((byte)x, (byte)y, (byte)z, 1);
                }

        fixed (RgbaByte* dataPtr = data)
        {
            GD.UpdateTexture(tex3D, (IntPtr)dataPtr, (uint)(data.Length * Unsafe.SizeOf<RgbaByte>()),
                0, 0, 0,
                tex3D.Width, tex3D.Height, tex3D.Depth,
                0, 0);
        }

        Texture staging = RF.CreateTexture(TextureDescription.Texture3D(
            16, 16, 16, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(tex3D, staging);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        MappedResourceView<RgbaByte> view = GD.Map<RgbaByte>(staging, MapMode.Read);
        for (int z = 0; z < tex3D.Depth; z++)
            for (int y = 0; y < tex3D.Height; y++)
                for (int x = 0; x < tex3D.Width; x++)
                {
                    Assert.Equal(new RgbaByte((byte)x, (byte)y, (byte)z, 1), view[x, y, z]);
                }
        GD.Unmap(staging);
    }

    [Fact]
    public unsafe void Copy_NonSquareTexture()
    {
        Texture src = RF.CreateTexture(
            TextureDescription.Texture2D(512, 128, 1, 1, PixelFormat.R8_UNorm, TextureUsage.Staging));
        byte[] data = Enumerable.Repeat((byte)255, (int)(src.Width * src.Height)).ToArray();
        fixed (byte* dataPtr = data)
        {
            GD.UpdateTexture(src, (IntPtr)dataPtr, (uint)data.Length,
                0, 0, 0,
                src.Width, src.Height, 1,
                0, 0);
        }

        Texture dst = RF.CreateTexture(
            TextureDescription.Texture2D(512, 128, 1, 1, PixelFormat.R8_UNorm, TextureUsage.Staging));
        byte[] data2 = Enumerable.Repeat((byte)100, (int)(dst.Width * dst.Height)).ToArray();
        fixed (byte* dataPtr2 = data2)
        {
            GD.UpdateTexture(dst, (IntPtr)dataPtr2, (uint)data2.Length,
                0, 0, 0,
                dst.Width, dst.Height, 1,
                0, 0);
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(src, dst);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        MappedResourceView<byte> readView = GD.Map<byte>(dst, MapMode.Read);
        for (uint y = 0; y < dst.Height; y++)
            for (uint x = 0; x < dst.Width; x++)
            {
                Assert.Equal(255, readView[x, y]);
            }

        GD.Unmap(dst);
    }

    [SkippableTheory]
    [MemberData(nameof(FormatCoverageData))]
    public unsafe void FormatCoverage_CopyThenRead(
        PixelFormat format, int rBits, int gBits, int bBits, int aBits,
        TextureType srcType,
        uint srcWidth, uint srcHeight, uint srcDepth, uint srcMipLevels, uint srcArrayLayers,
        TextureType dstType,
        uint dstWidth, uint dstHeight, uint dstDepth, uint dstMipLevels, uint dstArrayLayers,
        uint copyWidth, uint copyHeight, uint copyDepth,
        uint srcX, uint srcY, uint srcZ,
        uint srcMipLevel, uint srcArrayLayer,
        uint dstX, uint dstY, uint dstZ,
        uint dstMipLevel, uint dstArrayLayer)
    {
        Skip.IfNot(
            GD.GetPixelFormatSupport(format, srcType, TextureUsage.Staging),
            $"NV-SKIP-STAGING-FORMAT: {format}/{srcType} staging is unavailable on {GD.BackendType}.");

        Texture srcTex = RF.CreateTexture(new TextureDescription(
            srcWidth, srcHeight, srcDepth, srcMipLevels, srcArrayLayers,
            format, TextureUsage.Staging, srcType));

        TextureDataReaderWriter tdrw = new TextureDataReaderWriter(rBits, gBits, bBits, aBits);
        byte[] dataArray = tdrw.GetDataArray(srcWidth, srcHeight, srcDepth);
        long rowPitch = srcWidth * tdrw.PixelBytes;
        long depthPitch = rowPitch * srcHeight;
        fixed (byte* dataPtr = dataArray)
        {
            for (uint z = 0; z < srcDepth; z++)
            {
                for (uint y = 0; y < srcHeight; y++)
                {
                    for (uint x = 0; x < srcWidth; x++)
                    {
                        long offset = z * depthPitch + y * rowPitch + x * tdrw.PixelBytes;
                        WidePixel pixel = tdrw.GetTestPixel(x, y, z);
                        tdrw.WritePixel(dataPtr + offset, pixel);
                    }
                }
            }

            GD.UpdateTexture(
                srcTex, (IntPtr)dataPtr, (uint)dataArray.Length,
                0, 0, 0, srcWidth, srcHeight, srcDepth, 0, 0);
        }

        Texture dstTex = RF.CreateTexture(new TextureDescription(
            dstWidth, dstHeight, dstDepth, dstMipLevels, dstArrayLayers,
            format, TextureUsage.Staging, dstType));

        CommandList cl = RF.CreateCommandList();
        cl.Begin();

        cl.CopyTexture(
            srcTex, srcX, srcY, srcZ, srcMipLevel, srcArrayLayer,
            dstTex, dstX, dstY, dstZ, dstMipLevel, dstArrayLayer,
            copyWidth, copyHeight, copyDepth, 1);

        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        MappedResource map = GD.Map(dstTex, MapMode.Read);
        for (uint z = 0; z < copyDepth; z++)
        {
            for (uint y = 0; y < copyHeight; y++)
            {
                for (uint x = 0; x < copyWidth; x++)
                {
                    long offset = (z + dstZ) * map.DepthPitch
                        + (y + dstY) * map.RowPitch
                        + (x + dstX) * tdrw.PixelBytes;
                    WidePixel expected = tdrw.GetTestPixel(x, y, z);
                    WidePixel actual = tdrw.ReadPixel((byte*)map.Data + offset);
                    Assert.Equal(expected, actual);
                }
            }
        }

        GD.Unmap(dstTex);
    }

    public static IEnumerable<object[]> FormatCoverageData()
    {
        foreach (FormatProps props in s_allFormatProps)
        {
            yield return new object[]
            {
                props.Format, props.RedBits, props.GreenBits, props.BlueBits, props.AlphaBits,
                TextureType.Texture2D,
                64, 64, 1, 1, 1,
                TextureType.Texture2D,
                64, 64, 1, 1, 1,
                64, 64, 1,
                0, 0, 0,
                0, 0,
                0, 0, 0,
                0, 0
            };
        }
    }

    [Theory]
    [InlineData(TextureUsage.Sampled | TextureUsage.GenerateMipmaps)]
    [InlineData(TextureUsage.RenderTarget | TextureUsage.GenerateMipmaps)]
    [InlineData(TextureUsage.Storage | TextureUsage.GenerateMipmaps)]
    [InlineData(TextureUsage.Sampled | TextureUsage.RenderTarget | TextureUsage.GenerateMipmaps)]
    public unsafe void GenerateMipmaps(TextureUsage usage)
    {
        TextureDescription texDesc = TextureDescription.Texture2D(
            1024, 1024, 11, 1,
            PixelFormat.R32_G32_B32_A32_Float,
            usage);
        Texture tex = RF.CreateTexture(texDesc);

        texDesc.Usage = TextureUsage.Staging;
        Texture readback = RF.CreateTexture(texDesc);

        RgbaFloat[] pixelData = Enumerable.Repeat(RgbaFloat.Red, 1024 * 1024).ToArray();
        fixed (RgbaFloat* pixelDataPtr = pixelData)
        {
            GD.UpdateTexture(tex, (IntPtr)pixelDataPtr, 1024 * 1024 * 16, 0, 0, 0, 1024, 1024, 1, 0, 0);
        }

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.GenerateMipmaps(tex);
        cl.CopyTexture(tex, readback);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        for (uint level = 1; level < 11; level++)
        {
            MappedResourceView<RgbaFloat> readView = GD.Map<RgbaFloat>(readback, MapMode.Read, level);
            uint mipWidth = Math.Max(1, (uint)(tex.Width / Math.Pow(2, level)));
            uint mipHeight = Math.Max(1, (uint)(tex.Width / Math.Pow(2, level)));
            Assert.Equal(RgbaFloat.Red, readView[mipWidth - 1, mipHeight - 1]);
            GD.Unmap(readback, level);
        }
    }

    [Fact]
    public void CopyTexture_SmallCompressed()
    {
        Texture src = RF.CreateTexture(TextureDescription.Texture2D(16, 16, 4, 1, PixelFormat.BC3_UNorm, TextureUsage.Sampled));
        Texture dst = RF.CreateTexture(TextureDescription.Texture2D(16, 16, 4, 1, PixelFormat.BC3_UNorm, TextureUsage.Sampled));

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(
            src, 0, 0, 0, 3, 0,
            dst, 0, 0, 0, 3, 0,
            4, 4, 1, 1);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();
    }

    [SkippableFact]
    public void CopyTexture_SmallCompressed_ToStaging()
    {
        const PixelFormat format = PixelFormat.BC3_UNorm;
        Skip.IfNot(
            GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Sampled)
                && GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Staging),
            $"NV-SKIP-COMPRESSED-STAGING: {format} compressed staging readback is unavailable on {GD.BackendType}.");

        Texture src = RF.CreateTexture(TextureDescription.Texture2D(
            16, 16, 4, 1, format, TextureUsage.Sampled));
        Texture dst = RF.CreateTexture(TextureDescription.Texture2D(
            16, 16, 4, 1, format, TextureUsage.Staging));

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.CopyTexture(
            src, 0, 0, 0, 3, 0,
            dst, 0, 0, 0, 3, 0,
            4, 4, 1, 1);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();
    }

    [Theory]
    [InlineData(PixelFormat.BC1_Rgb_UNorm)]
    [InlineData(PixelFormat.BC1_Rgb_UNorm_SRgb)]
    [InlineData(PixelFormat.BC1_Rgba_UNorm)]
    [InlineData(PixelFormat.BC1_Rgba_UNorm_SRgb)]
    [InlineData(PixelFormat.BC2_UNorm)]
    [InlineData(PixelFormat.BC2_UNorm_SRgb)]
    [InlineData(PixelFormat.BC3_UNorm)]
    [InlineData(PixelFormat.BC3_UNorm_SRgb)]
    [InlineData(PixelFormat.BC4_UNorm)]
    [InlineData(PixelFormat.BC4_SNorm)]
    [InlineData(PixelFormat.BC5_UNorm)]
    [InlineData(PixelFormat.BC5_SNorm)]
    [InlineData(PixelFormat.BC7_UNorm)]
    [InlineData(PixelFormat.BC7_UNorm_SRgb)]
    public void CreateSmallTexture(PixelFormat format)
    {
        Texture tex = RF.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, format, TextureUsage.Sampled));
        Assert.Equal(1u, tex.Width);
        Assert.Equal(1u, tex.Height);
    }

    private static readonly FormatProps[] s_allFormatProps =
    {
        new FormatProps(PixelFormat.R8_UNorm, 8, 0, 0, 0),
        new FormatProps(PixelFormat.R8_SNorm, 8, 0, 0, 0),
        new FormatProps(PixelFormat.R8_UInt, 8, 0, 0, 0),
        new FormatProps(PixelFormat.R8_SInt, 8, 0, 0, 0),

        new FormatProps(PixelFormat.R16_UNorm, 16, 0, 0, 0),
        new FormatProps(PixelFormat.R16_SNorm, 16, 0, 0, 0),
        new FormatProps(PixelFormat.R16_UInt, 16, 0, 0, 0),
        new FormatProps(PixelFormat.R16_SInt, 16, 0, 0, 0),
        new FormatProps(PixelFormat.R16_Float, 16, 0, 0, 0),

        new FormatProps(PixelFormat.R32_UInt, 32, 0, 0, 0),
        new FormatProps(PixelFormat.R32_SInt, 32, 0, 0, 0),
        new FormatProps(PixelFormat.R32_Float, 32, 0, 0, 0),

        new FormatProps(PixelFormat.R8_G8_UNorm, 8, 8, 0, 0),
        new FormatProps(PixelFormat.R8_G8_SNorm, 8, 8, 0, 0),
        new FormatProps(PixelFormat.R8_G8_UInt, 8, 8, 0, 0),
        new FormatProps(PixelFormat.R8_G8_SInt, 8, 8, 0, 0),

        new FormatProps(PixelFormat.R16_G16_UNorm, 16, 16, 0, 0),
        new FormatProps(PixelFormat.R16_G16_SNorm, 16, 16, 0, 0),
        new FormatProps(PixelFormat.R16_G16_UInt, 16, 16, 0, 0),
        new FormatProps(PixelFormat.R16_G16_SInt, 16, 16, 0, 0),
        new FormatProps(PixelFormat.R16_G16_Float, 16, 16, 0, 0),

        new FormatProps(PixelFormat.R32_G32_UInt, 32, 32, 0, 0),
        new FormatProps(PixelFormat.R32_G32_SInt, 32, 32, 0, 0),
        new FormatProps(PixelFormat.R32_G32_Float, 32, 32, 0, 0),

        new FormatProps(PixelFormat.B8_G8_R8_A8_UNorm, 8, 8, 8, 8),
        new FormatProps(PixelFormat.R8_G8_B8_A8_UNorm, 8, 8, 8, 8),
        new FormatProps(PixelFormat.R8_G8_B8_A8_SNorm, 8, 8, 8, 8),
        new FormatProps(PixelFormat.R8_G8_B8_A8_UInt, 8, 8, 8, 8),
        new FormatProps(PixelFormat.R8_G8_B8_A8_SInt, 8, 8, 8, 8),

        new FormatProps(PixelFormat.R16_G16_B16_A16_UNorm, 16, 16, 16, 16),
        new FormatProps(PixelFormat.R16_G16_B16_A16_SNorm, 16, 16, 16, 16),
        new FormatProps(PixelFormat.R16_G16_B16_A16_UInt, 16, 16, 16, 16),
        new FormatProps(PixelFormat.R16_G16_B16_A16_SInt, 16, 16, 16, 16),
        new FormatProps(PixelFormat.R16_G16_B16_A16_Float, 16, 16, 16, 16),

        new FormatProps(PixelFormat.R32_G32_B32_A32_UInt, 32, 32, 32, 32),
        new FormatProps(PixelFormat.R32_G32_B32_A32_SInt, 32, 32, 32, 32),
        new FormatProps(PixelFormat.R32_G32_B32_A32_Float, 32, 32, 32, 32),

        new FormatProps(PixelFormat.R10_G10_B10_A2_UInt, 10, 10, 10, 2),
        new FormatProps(PixelFormat.R10_G10_B10_A2_UNorm, 10, 10, 10, 2),
        new FormatProps(PixelFormat.R11_G11_B10_Float, 11, 11, 10, 0)
    };

    struct FormatProps
    {
        public readonly PixelFormat Format;
        public readonly int RedBits;
        public readonly int BlueBits;
        public readonly int GreenBits;
        public readonly int AlphaBits;

        public FormatProps(PixelFormat format, int redBits, int blueBits, int greenBits, int alphaBits)
        {
            Format = format;
            RedBits = redBits;
            BlueBits = blueBits;
            GreenBits = greenBits;
            AlphaBits = alphaBits;
        }
    }
}

#if TEST_VULKAN
[Trait("Backend", "Vulkan")]
public class VulkanTextureTests : TextureTestBase<VulkanDeviceCreator>
{
    [Theory]
    [InlineData(PixelFormat.D24_UNorm_S8_UInt)]
    [InlineData(PixelFormat.D32_Float_S8_UInt)]
    public void PackedDepthStencilStagingIsRejected(PixelFormat format)
    {
        Assert.False(GD.GetPixelFormatSupport(
            format,
            TextureType.Texture2D,
            TextureUsage.Staging));

        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => RF.CreateTexture(TextureDescription.Texture2D(
                4,
                4,
                1,
                1,
                format,
                TextureUsage.Staging)));
        Assert.Contains("packed depth-stencil plane layout", exception.Message);
    }

    private const uint MultiLayerCopyWidth = 13;
    private const uint MultiLayerCopyHeight = 11;
    private const uint MultiLayerCopyMipLevels = 4;
    private const uint MultiLayerCopyArrayLayers = 3;
    private const byte MultiLayerCopySentinel = 0xE3;

    [SkippableTheory]
    [InlineData(PixelFormat.BC1_Rgba_UNorm)]
    [InlineData(PixelFormat.BC3_UNorm)]
    public void CompressedStagingSupportUsesBufferImageTransferCapability(
        PixelFormat format)
    {
        Skip.IfNot(
            GD.GetPixelFormatSupport(
                format,
                TextureType.Texture2D,
                TextureUsage.Sampled),
            $"NV-SKIP-COMPRESSED-SAMPLING: {format} sampling is unavailable on {GD.BackendType}.");

        Assert.True(
            GD.GetPixelFormatSupport(
                format,
                TextureType.Texture2D,
                TextureUsage.Staging,
                out PixelFormatProperties properties),
            $"Vulkan exposes sampled {format}, so its buffer-backed staging representation must support transfers to and from an optimal image.");
        Assert.True(
            properties.IsSampleCountSupported(TextureSampleCount.Count1));
        Assert.False(
            properties.IsSampleCountSupported(TextureSampleCount.Count2));
    }

    [SkippableTheory]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 8u)]
    [InlineData(PixelFormat.BC3_UNorm, 16u)]
    public unsafe void Copy_Compressed_StagingToOptimalUsesMipChainArrayStride(
        PixelFormat format,
        uint blockSizeInBytes)
    {
        AssertCompressedMultiMipMultiLayerCopy(
            format,
            blockSizeInBytes,
            TextureUsage.Staging,
            TextureUsage.Sampled);
    }

    [SkippableTheory]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 8u)]
    [InlineData(PixelFormat.BC3_UNorm, 16u)]
    public unsafe void Copy_Compressed_OptimalToStagingUsesMipChainArrayStride(
        PixelFormat format,
        uint blockSizeInBytes)
    {
        AssertCompressedMultiMipMultiLayerCopy(
            format,
            blockSizeInBytes,
            TextureUsage.Sampled,
            TextureUsage.Staging);
    }

    [SkippableTheory]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 8u)]
    [InlineData(PixelFormat.BC3_UNorm, 16u)]
    public unsafe void Copy_Compressed_StagingToStagingSeparatesLayerAndDepthStrides(
        PixelFormat format,
        uint blockSizeInBytes)
    {
        AssertCompressedMultiMipMultiLayerCopy(
            format,
            blockSizeInBytes,
            TextureUsage.Staging,
            TextureUsage.Staging);
    }

    private unsafe void AssertCompressedMultiMipMultiLayerCopy(
        PixelFormat format,
        uint blockSizeInBytes,
        TextureUsage sourceUsage,
        TextureUsage destinationUsage)
    {
        Skip.IfNot(
            GD.GetPixelFormatSupport(
                format,
                TextureType.Texture2D,
                TextureUsage.Sampled),
            $"NV-SKIP-COMPRESSED-SAMPLING: {format} sampling is unavailable on {GD.BackendType}.");
        Skip.IfNot(
            GD.GetPixelFormatSupport(
                format,
                TextureType.Texture2D,
                TextureUsage.Staging),
            $"NV-SKIP-COMPRESSED-STAGING: {format} compressed staging transfers are unavailable on {GD.BackendType}.");
        Assert.Equal(
            blockSizeInBytes,
            FormatHelpers.GetBlockSizeInBytes(format));

        Texture source = RF.CreateTexture(TextureDescription.Texture2D(
            MultiLayerCopyWidth,
            MultiLayerCopyHeight,
            MultiLayerCopyMipLevels,
            MultiLayerCopyArrayLayers,
            format,
            sourceUsage));
        Texture destination = RF.CreateTexture(TextureDescription.Texture2D(
            MultiLayerCopyWidth,
            MultiLayerCopyHeight,
            MultiLayerCopyMipLevels,
            MultiLayerCopyArrayLayers,
            format,
            destinationUsage));

        for (uint arrayLayer = 0;
            arrayLayer < MultiLayerCopyArrayLayers;
            arrayLayer++)
        {
            for (uint mipLevel = 0;
                mipLevel < MultiLayerCopyMipLevels;
                mipLevel++)
            {
                byte[] sourceData = CreateCompressedSubresourcePattern(
                    format,
                    mipLevel,
                    arrayLayer);
                WriteCompressedSubresource(
                    source,
                    sourceUsage,
                    mipLevel,
                    arrayLayer,
                    sourceData);

                byte[] destinationData = new byte[sourceData.Length];
                Array.Fill(destinationData, MultiLayerCopySentinel);
                WriteCompressedSubresource(
                    destination,
                    destinationUsage,
                    mipLevel,
                    arrayLayer,
                    destinationData);
            }
        }
        GD.WaitForIdle();

        CommandList copy = RF.CreateCommandList();
        copy.Begin();
        CopyCompressedMipLayers(copy, source, destination, mipLevel: 0);
        CopyCompressedMipLayers(copy, source, destination, mipLevel: 2);
        copy.End();
        GD.SubmitCommands(copy);
        GD.WaitForIdle();

        Texture inspected = destination;
        if (destinationUsage != TextureUsage.Staging)
        {
            inspected = RF.CreateTexture(TextureDescription.Texture2D(
                MultiLayerCopyWidth,
                MultiLayerCopyHeight,
                MultiLayerCopyMipLevels,
                MultiLayerCopyArrayLayers,
                format,
                TextureUsage.Staging));
            CommandList readback = RF.CreateCommandList();
            readback.Begin();
            for (uint arrayLayer = 0;
                arrayLayer < MultiLayerCopyArrayLayers;
                arrayLayer++)
            {
                for (uint mipLevel = 0;
                    mipLevel < MultiLayerCopyMipLevels;
                    mipLevel++)
                {
                    Util.GetMipDimensions(
                        destination,
                        mipLevel,
                        out uint mipWidth,
                        out uint mipHeight,
                        out _);
                    readback.CopyTexture(
                        destination,
                        0, 0, 0,
                        mipLevel,
                        arrayLayer,
                        inspected,
                        0, 0, 0,
                        mipLevel,
                        arrayLayer,
                        mipWidth,
                        mipHeight,
                        1,
                        1);
                }
            }
            readback.End();
            GD.SubmitCommands(readback);
            GD.WaitForIdle();
        }

        for (uint arrayLayer = 0;
            arrayLayer < MultiLayerCopyArrayLayers;
            arrayLayer++)
        {
            for (uint mipLevel = 0;
                mipLevel < MultiLayerCopyMipLevels;
                mipLevel++)
            {
                bool copiedMip = mipLevel == 0 || mipLevel == 2;
                byte[] expected = copiedMip && arrayLayer < 2
                    ? CreateCompressedSubresourcePattern(
                        format,
                        mipLevel,
                        arrayLayer + 1)
                    : CreateCompressedSubresourceSentinel(format, mipLevel);
                AssertCompressedSubresourceEquals(
                    inspected,
                    mipLevel,
                    arrayLayer,
                    expected);
            }
        }
    }

    private static void CopyCompressedMipLayers(
        CommandList commandList,
        Texture source,
        Texture destination,
        uint mipLevel)
    {
        Util.GetMipDimensions(
            source,
            mipLevel,
            out uint mipWidth,
            out uint mipHeight,
            out _);
        commandList.CopyTexture(
            source,
            0, 0, 0,
            mipLevel,
            srcBaseArrayLayer: 1,
            destination,
            0, 0, 0,
            mipLevel,
            dstBaseArrayLayer: 0,
            mipWidth,
            mipHeight,
            depth: 1,
            layerCount: 2);
    }

    private static byte[] CreateCompressedSubresourcePattern(
        PixelFormat format,
        uint mipLevel,
        uint arrayLayer)
    {
        byte[] data = CreateCompressedSubresourceSentinel(format, mipLevel);
        for (uint index = 0; index < data.Length; index++)
        {
            data[index] = unchecked((byte)(
                0x17u
                + (arrayLayer * 67u)
                + (mipLevel * 29u)
                + (index * 19u)));
        }
        return data;
    }

    private static byte[] CreateCompressedSubresourceSentinel(
        PixelFormat format,
        uint mipLevel)
    {
        uint mipWidth = Util.GetDimension(MultiLayerCopyWidth, mipLevel);
        uint mipHeight = Util.GetDimension(MultiLayerCopyHeight, mipLevel);
        byte[] data = new byte[checked((int)FormatHelpers.GetRegionSize(
            mipWidth,
            mipHeight,
            depth: 1,
            format: format))];
        Array.Fill(data, MultiLayerCopySentinel);
        return data;
    }

    private unsafe void WriteCompressedSubresource(
        Texture texture,
        TextureUsage usage,
        uint mipLevel,
        uint arrayLayer,
        byte[] data)
    {
        Util.GetMipDimensions(
            texture,
            mipLevel,
            out uint mipWidth,
            out uint mipHeight,
            out _);
        if (usage != TextureUsage.Staging)
        {
            GD.UpdateTexture(
                texture,
                data,
                0, 0, 0,
                mipWidth,
                mipHeight,
                1,
                mipLevel,
                arrayLayer);
            return;
        }

        uint subresource = texture.CalculateSubresource(mipLevel, arrayLayer);
        MappedResource mapped = GD.Map(texture, MapMode.Write, subresource);
        try
        {
            CopyDenseCompressedDataToMappedResource(
                mapped,
                mipWidth,
                mipHeight,
                texture.Format,
                data);
        }
        finally
        {
            GD.Unmap(texture, subresource);
        }
    }

    private unsafe void AssertCompressedSubresourceEquals(
        Texture texture,
        uint mipLevel,
        uint arrayLayer,
        byte[] expected)
    {
        Util.GetMipDimensions(
            texture,
            mipLevel,
            out uint mipWidth,
            out uint mipHeight,
            out _);
        uint rowSize = FormatHelpers.GetRowPitch(mipWidth, texture.Format);
        uint rowCount = FormatHelpers.GetNumRows(mipHeight, texture.Format);
        Assert.Equal(checked((int)(rowSize * rowCount)), expected.Length);

        uint subresource = texture.CalculateSubresource(mipLevel, arrayLayer);
        MappedResource mapped = GD.Map(texture, MapMode.Read, subresource);
        try
        {
            Assert.True(rowSize <= mapped.RowPitch);
            byte* mappedBase = (byte*)mapped.Data;
            for (uint row = 0; row < rowCount; row++)
            {
                for (uint column = 0; column < rowSize; column++)
                {
                    byte actual = *(mappedBase + checked((nint)(
                        ((nuint)row * mapped.RowPitch) + column)));
                    byte expectedByte = expected[checked((int)(
                        (row * rowSize) + column))];
                    Assert.True(
                        expectedByte == actual,
                        $"Expected 0x{expectedByte:X2} but found 0x{actual:X2} at mip {mipLevel}, layer {arrayLayer}, row {row}, byte {column}.");
                }
            }
        }
        finally
        {
            GD.Unmap(texture, subresource);
        }
    }

    private static unsafe void CopyDenseCompressedDataToMappedResource(
        MappedResource mapped,
        uint width,
        uint height,
        PixelFormat format,
        byte[] data)
    {
        uint rowSize = FormatHelpers.GetRowPitch(width, format);
        uint rowCount = FormatHelpers.GetNumRows(height, format);
        Assert.Equal(checked((int)(rowSize * rowCount)), data.Length);
        Assert.True(rowSize <= mapped.RowPitch);

        byte* mappedBase = (byte*)mapped.Data;
        for (uint row = 0; row < rowCount; row++)
        {
            data.AsSpan(
                checked((int)(row * rowSize)),
                checked((int)rowSize)).CopyTo(new Span<byte>(
                    mappedBase + checked((nint)((nuint)row * mapped.RowPitch)),
                    checked((int)rowSize)));
        }
    }
}
#endif
#if TEST_D3D11
[Trait("Backend", "D3D11")]
public class D3D11TextureTests : TextureTestBase<D3D11DeviceCreator>
{
    [Fact]
    public unsafe void Copy_Compressed_OddPhysicalMipEdgeBoxCopiesExactly()
    {
        const PixelFormat format = PixelFormat.BC3_UNorm;
        Assert.True(
            GD.GetPixelFormatSupport(
                format,
                TextureType.Texture2D,
                TextureUsage.Sampled),
            "The D3D11 qualification device must support sampled BC3 textures.");
        Assert.True(
            GD.GetPixelFormatSupport(
                format,
                TextureType.Texture2D,
                TextureUsage.Staging),
            "The D3D11 qualification device must support staging BC3 textures.");

        // A 13x11 logical texture is physically 16x12 on D3D11. Its second mip
        // is therefore physically 4x3 but logically 3x2. This one-block edge
        // region requires D3D11's copy box to expand to and clamp at (4, 3),
        // independently of the Vulkan buffer-image stride implementation.
        AssertCompressedStagingToOptimalCopy(
            format,
            blockSizeInBytes: 16,
            textureWidth: 13,
            textureHeight: 11,
            mipLevels: 4,
            arrayLayers: 2,
            regions: new[]
            {
                new CompressedCopyRegion(
                    mipLevel: 2,
                    arrayLayer: 1,
                    x: 0,
                    y: 0,
                    width: 3,
                    height: 2,
                    patternSeed: 0xD3),
            });
    }
}
#endif
#if TEST_OPENGL
[Trait("Backend", "OpenGL")]
public class OpenGLTextureTests : TextureTestBase<OpenGLDeviceCreator>
{
    [SkippableFact]
    public unsafe void Copy_Compressed_3DDepthRoundaboutCopiesEverySlice()
    {
        const PixelFormat format = PixelFormat.BC1_Rgba_UNorm;
        Skip.IfNot(
            GD.GetPixelFormatSupport(
                format,
                TextureType.Texture3D,
                TextureUsage.Sampled)
            && GD.GetPixelFormatSupport(
                format,
                TextureType.Texture3D,
                TextureUsage.Staging),
            $"NV-SKIP-COMPRESSED-3D-ROUNDABOUT: {format} compressed 3D copy/readback is unavailable on {GD.BackendType}.");

        AssertCompressed3DRoundaboutCopy(
            width: 8,
            height: 8,
            textureDepth: 4,
            sourceX: 0,
            sourceY: 0,
            sourceZ: 1,
            destinationX: 0,
            destinationY: 0,
            destinationZ: 0,
            copyWidth: 8,
            copyHeight: 8,
            copyDepth: 2,
            patternSeed: 0x21);
        AssertCompressed3DRoundaboutCopy(
            width: 13,
            height: 11,
            textureDepth: 4,
            sourceX: 12,
            sourceY: 8,
            sourceZ: 1,
            destinationX: 12,
            destinationY: 8,
            destinationZ: 0,
            copyWidth: 4,
            copyHeight: 4,
            copyDepth: 2,
            patternSeed: 0x91);
        AssertCompressed3DRoundaboutCopy(
            width: 13,
            height: 11,
            textureDepth: 4,
            sourceX: 0,
            sourceY: 4,
            sourceZ: 0,
            destinationX: 8,
            destinationY: 0,
            destinationZ: 1,
            copyWidth: 4,
            copyHeight: 4,
            copyDepth: 2,
            patternSeed: 0xC3);
    }

    private unsafe void AssertCompressed3DRoundaboutCopy(
        uint width,
        uint height,
        uint textureDepth,
        uint sourceX,
        uint sourceY,
        uint sourceZ,
        uint destinationX,
        uint destinationY,
        uint destinationZ,
        uint copyWidth,
        uint copyHeight,
        uint copyDepth,
        byte patternSeed)
    {
        const PixelFormat format = PixelFormat.BC1_Rgba_UNorm;
        const uint blockExtent = 4;
        const uint blockSizeInBytes = 8;
        const byte untouched = 0xD7;
        uint rowPitch = FormatHelpers.GetRowPitch(width, format);
        uint depthPitch = FormatHelpers.GetDepthPitch(
            rowPitch,
            height,
            format);
        uint blockRows = FormatHelpers.GetNumRows(height, format);
        uint sourceBlockX = sourceX / blockExtent;
        uint sourceBlockY = sourceY / blockExtent;
        uint destinationBlockX = destinationX / blockExtent;
        uint destinationBlockY = destinationY / blockExtent;
        uint copyBlockColumns = (copyWidth + blockExtent - 1) / blockExtent;
        uint copyBlockRows = (copyHeight + blockExtent - 1) / blockExtent;

        TextureDescription description = TextureDescription.Texture3D(
            width,
            height,
            textureDepth,
            1,
            format,
            TextureUsage.Sampled);
        Texture source = RF.CreateTexture(description);
        Texture destination = RF.CreateTexture(description);
        description.Usage = TextureUsage.Staging;
        Texture capture = RF.CreateTexture(description);

        byte[] sourceData = new byte[checked((int)(depthPitch * textureDepth))];
        byte[] destinationData = new byte[sourceData.Length];
        Array.Fill(destinationData, untouched);
        for (uint slice = 0; slice < textureDepth; slice++)
        {
            for (uint index = 0; index < depthPitch; index++)
            {
                sourceData[checked((int)(slice * depthPitch + index))] =
                    unchecked((byte)(patternSeed + slice * 47u + index));
            }
        }

        GD.UpdateTexture(
            source,
            sourceData,
            0, 0, 0,
            width, height, textureDepth,
            0, 0);
        GD.UpdateTexture(
            destination,
            destinationData,
            0, 0, 0,
            width, height, textureDepth,
            0, 0);

        CommandList copy = RF.CreateCommandList();
        copy.Begin();
        copy.CopyTexture(
            source,
            sourceX, sourceY, sourceZ,
            0, 0,
            destination,
            destinationX, destinationY, destinationZ,
            0, 0,
            copyWidth, copyHeight, copyDepth,
            1);
        copy.End();
        GD.SubmitCommands(copy);
        GD.WaitForIdle();

        // Read back the complete mip. Affected desktop GL drivers reject
        // depth-one compressed 3D subimages for the same native alignment
        // reason as the partial copy under test. The full-depth readback does
        // not use the partial destination read-modify-write branch, and an
        // omitted or mispositioned partial copy remains visible in its bytes.
        CommandList readback = RF.CreateCommandList();
        readback.Begin();
        readback.CopyTexture(
            destination,
            0, 0, 0,
            0, 0,
            capture,
            0, 0, 0,
            0, 0,
            width, height, textureDepth,
            1);
        readback.End();
        GD.SubmitCommands(readback);
        GD.WaitForIdle();

        MappedResource mapped = GD.Map(capture, MapMode.Read);
        try
        {
            byte* mappedBase = (byte*)mapped.Data;
            for (uint slice = 0; slice < textureDepth; slice++)
            {
                bool copied =
                    slice >= destinationZ &&
                    slice < destinationZ + copyDepth;
                uint sourceSlice = copied
                    ? sourceZ + slice - destinationZ
                    : 0;
                for (uint blockRow = 0; blockRow < blockRows; blockRow++)
                {
                    for (uint rowByte = 0; rowByte < rowPitch; rowByte++)
                    {
                        uint blockColumn = rowByte / blockSizeInBytes;
                        bool copiedBlock = copied
                            && blockRow >= destinationBlockY
                            && blockRow < destinationBlockY + copyBlockRows
                            && blockColumn >= destinationBlockX
                            && blockColumn < destinationBlockX + copyBlockColumns;
                        byte expected = untouched;
                        if (copiedBlock)
                        {
                            uint byteWithinBlock = rowByte % blockSizeInBytes;
                            uint sourceBlockRow = sourceBlockY
                                + blockRow - destinationBlockY;
                            uint sourceBlockColumn = sourceBlockX
                                + blockColumn - destinationBlockX;
                            expected = sourceData[checked((int)(
                                sourceSlice * depthPitch +
                                sourceBlockRow * rowPitch +
                                sourceBlockColumn * blockSizeInBytes +
                                byteWithinBlock))];
                        }
                        nuint mappedOffset = checked(
                            ((nuint)slice * mapped.DepthPitch) +
                            ((nuint)blockRow * mapped.RowPitch) +
                            rowByte);
                        byte actual = *(mappedBase + checked((nint)mappedOffset));
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }
        finally
        {
            GD.Unmap(capture);
        }
    }
}
#endif
#if TEST_OPENGLES
[Trait("Backend", "OpenGLES")]
public class OpenGLESTextureTests : TextureTestBase<OpenGLESDeviceCreator>
{
    [Fact]
    public void CompressedStagingReadback_IsReportedUnsupported()
    {
        const PixelFormat format = PixelFormat.BC3_UNorm;
        Assert.False(GD.GetPixelFormatSupport(format, TextureType.Texture2D, TextureUsage.Staging));

        Texture texture = RF.CreateTexture(TextureDescription.Texture2D(
            16, 16, 1, 1, format, TextureUsage.Staging));
        NeoVeldridException exception = Assert.Throws<NeoVeldridException>(
            () => GD.Map(texture, MapMode.Read));

        Assert.Contains("not supported by the OpenGL ES backend", exception.Message);
    }
}
#endif
