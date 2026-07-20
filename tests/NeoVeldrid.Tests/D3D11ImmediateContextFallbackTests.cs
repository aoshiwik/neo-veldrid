#if TEST_D3D11
using System;
using Xunit;

namespace NeoVeldrid.Tests;

public sealed class D3D11ForcedSoftwareCommandListDeviceCreator : GraphicsDeviceCreator
{
    public void CreateGraphicsDevice(
        out NeoVeldrid.Sdl2.Sdl2Window window,
        out GraphicsDevice gd)
    {
        window = null;
        gd = GraphicsDevice.CreateD3D11(
            new GraphicsDeviceOptions(debug: true),
            new D3D11DeviceOptions
            {
                DeferredTextureUploadMode =
                    D3D11DeferredTextureUploadMode.ForceSoftwareCommandListEmulation,
            });
    }
}

[Trait("Backend", "D3D11")]
public unsafe sealed class D3D11ImmediateContextFallbackTests
    : GraphicsDeviceTestBase<D3D11ForcedSoftwareCommandListDeviceCreator>
{
    [Fact]
    public void OrderedUncompressed2DUploadsUseForcedFallbackAndReadBackExactly()
    {
        const uint width = 8;
        const uint height = 7;
        BackendInfoD3D11 info = GetForcedFallbackInfo();
        ulong executionCountBefore = info.DeferredTextureUploadExecutionCount;
        ulong rebaseCountBefore = info.DeferredTextureUploadRebaseCount;
        int traceCountBefore =
            info.GetDeferredTextureUploadPlaybackTraces().Length;

        Texture destination = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled));
        Texture capture = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Staging));

        byte[] initial = new byte[checked((int)(width * height))];
        for (int index = 0; index < initial.Length; index++)
            initial[index] = checked((byte)(17 + index));

        const uint patchX = 2;
        const uint patchY = 3;
        const uint patchWidth = 3;
        const uint patchHeight = 2;
        byte[] patch = { 201, 202, 203, 211, 212, 213 };
        byte[] expected = (byte[])initial.Clone();
        for (uint y = 0; y < patchHeight; y++)
        {
            for (uint x = 0; x < patchWidth; x++)
            {
                expected[checked((int)((patchY + y) * width + patchX + x))] =
                    patch[checked((int)(y * patchWidth + x))];
            }
        }

        CommandList commandList = RF.CreateCommandList();
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            initial,
            0, 0, 0,
            width, height, 1,
            0, 0);
        commandList.UpdateTexture(
            destination,
            patch,
            patchX, patchY, 0,
            patchWidth, patchHeight, 1,
            0, 0);
        commandList.CopyTexture(destination, capture);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.Equal(
            executionCountBefore + 2,
            info.DeferredTextureUploadExecutionCount);
        Assert.Equal(
            rebaseCountBefore + 1,
            info.DeferredTextureUploadRebaseCount);
        D3D11DeferredTextureUploadPlaybackTrace[] traces =
            GetNewPlaybackTraces(info, traceCountBefore, expectedCount: 2);
        AssertPlaybackTrace(
            info,
            traces[0],
            expectedDelta: 0,
            destinationX: 0,
            destinationY: 0,
            destinationZ: 0,
            hasDestinationBox: false,
            destinationBoxRight: 0,
            destinationBoxBottom: 0,
            destinationBoxBack: 0,
            rowPitch: 8,
            depthPitch: 56,
            format: PixelFormat.R8_UNorm);
        AssertPlaybackTrace(
            info,
            traces[1],
            expectedDelta: 11,
            destinationX: patchX,
            destinationY: patchY,
            destinationZ: 0,
            hasDestinationBox: true,
            destinationBoxRight: 5,
            destinationBoxBottom: 5,
            destinationBoxBack: 1,
            rowPitch: 3,
            depthPitch: 6,
            format: PixelFormat.R8_UNorm);
        AssertR8TextureEquals(capture, expected, width, height, depth: 1);
    }

    [Fact]
    public void OrderedNonzeroZ3DUploadsUseForcedFallbackAndReadBackExactly()
    {
        const uint width = 6;
        const uint height = 5;
        const uint depth = 5;
        BackendInfoD3D11 info = GetForcedFallbackInfo();
        ulong executionCountBefore = info.DeferredTextureUploadExecutionCount;
        ulong rebaseCountBefore = info.DeferredTextureUploadRebaseCount;
        int traceCountBefore =
            info.GetDeferredTextureUploadPlaybackTraces().Length;

        Texture destination = RF.CreateTexture(TextureDescription.Texture3D(
            width,
            height,
            depth,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled));
        Texture capture = RF.CreateTexture(TextureDescription.Texture3D(
            width,
            height,
            depth,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Staging));

        byte[] initial = new byte[checked((int)(width * height * depth))];
        Array.Fill(initial, (byte)13);

        const uint patchX = 1;
        const uint patchY = 2;
        const uint patchZ = 2;
        const uint patchWidth = 3;
        const uint patchHeight = 2;
        const uint patchDepth = 2;
        byte[] patch = new byte[checked((int)(patchWidth * patchHeight * patchDepth))];
        for (int index = 0; index < patch.Length; index++)
            patch[index] = checked((byte)(101 + index));

        byte[] expected = (byte[])initial.Clone();
        for (uint z = 0; z < patchDepth; z++)
        {
            for (uint y = 0; y < patchHeight; y++)
            {
                for (uint x = 0; x < patchWidth; x++)
                {
                    int sourceIndex = checked((int)(
                        (z * patchHeight * patchWidth) +
                        (y * patchWidth) +
                        x));
                    int destinationIndex = checked((int)(
                        ((patchZ + z) * height * width) +
                        ((patchY + y) * width) +
                        patchX + x));
                    expected[destinationIndex] = patch[sourceIndex];
                }
            }
        }

        CommandList commandList = RF.CreateCommandList();
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            initial,
            0, 0, 0,
            width, height, depth,
            0, 0);
        commandList.UpdateTexture(
            destination,
            patch,
            patchX, patchY, patchZ,
            patchWidth, patchHeight, patchDepth,
            0, 0);
        commandList.CopyTexture(destination, capture);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.Equal(
            executionCountBefore + 2,
            info.DeferredTextureUploadExecutionCount);
        Assert.Equal(
            rebaseCountBefore + 1,
            info.DeferredTextureUploadRebaseCount);
        D3D11DeferredTextureUploadPlaybackTrace[] traces =
            GetNewPlaybackTraces(info, traceCountBefore, expectedCount: 2);
        AssertPlaybackTrace(
            info,
            traces[0],
            expectedDelta: 0,
            destinationX: 0,
            destinationY: 0,
            destinationZ: 0,
            hasDestinationBox: false,
            destinationBoxRight: 0,
            destinationBoxBottom: 0,
            destinationBoxBack: 0,
            rowPitch: 6,
            depthPitch: 30,
            format: PixelFormat.R8_UNorm);
        AssertPlaybackTrace(
            info,
            traces[1],
            expectedDelta: 19,
            destinationX: patchX,
            destinationY: patchY,
            destinationZ: patchZ,
            hasDestinationBox: true,
            destinationBoxRight: 4,
            destinationBoxBottom: 4,
            destinationBoxBack: 4,
            rowPitch: 3,
            depthPitch: 6,
            format: PixelFormat.R8_UNorm);
        AssertR8TextureEquals(capture, expected, width, height, depth);
    }

    [Fact]
    public void OrderedCompressedUploadsUseForcedFallbackAndReadBackExactly()
    {
        const PixelFormat format = PixelFormat.BC1_Rgba_UNorm;
        const uint width = 10;
        const uint height = 10;
        const uint storageRowCount = 3;
        const uint storageRowSize = 24;
        BackendInfoD3D11 info = GetForcedFallbackInfo();
        ulong executionCountBefore = info.DeferredTextureUploadExecutionCount;
        ulong rebaseCountBefore = info.DeferredTextureUploadRebaseCount;
        int traceCountBefore =
            info.GetDeferredTextureUploadPlaybackTraces().Length;

        Assert.True(GD.GetPixelFormatSupport(
            format,
            TextureType.Texture2D,
            TextureUsage.Sampled));
        Assert.True(GD.GetPixelFormatSupport(
            format,
            TextureType.Texture2D,
            TextureUsage.Staging));

        Texture destination = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            format,
            TextureUsage.Sampled));
        Texture capture = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            format,
            TextureUsage.Staging));

        byte[] initial = new byte[storageRowCount * storageRowSize];
        for (int index = 0; index < initial.Length; index++)
            initial[index] = checked((byte)(31 + index));

        const uint patchX = 4;
        const uint patchY = 4;
        const uint patchWidth = 6;
        const uint patchHeight = 6;
        const uint patchStorageRowCount = 2;
        const uint patchStorageRowSize = 16;
        byte[] patch = new byte[patchStorageRowCount * patchStorageRowSize];
        for (int index = 0; index < patch.Length; index++)
            patch[index] = checked((byte)(151 + index));

        byte[] expected = (byte[])initial.Clone();
        for (uint row = 0; row < patchStorageRowCount; row++)
        {
            Array.Copy(
                patch,
                checked((int)(row * patchStorageRowSize)),
                expected,
                checked((int)((row + 1) * storageRowSize + 8)),
                checked((int)patchStorageRowSize));
        }

        CommandList commandList = RF.CreateCommandList();
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            initial,
            0, 0, 0,
            width, height, 1,
            0, 0);
        commandList.UpdateTexture(
            destination,
            patch,
            patchX, patchY, 0,
            patchWidth, patchHeight, 1,
            0, 0);
        commandList.CopyTexture(destination, capture);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.Equal(
            executionCountBefore + 2,
            info.DeferredTextureUploadExecutionCount);
        Assert.Equal(
            rebaseCountBefore + 1,
            info.DeferredTextureUploadRebaseCount);
        D3D11DeferredTextureUploadPlaybackTrace[] traces =
            GetNewPlaybackTraces(info, traceCountBefore, expectedCount: 2);
        AssertPlaybackTrace(
            info,
            traces[0],
            expectedDelta: 0,
            destinationX: 0,
            destinationY: 0,
            destinationZ: 0,
            hasDestinationBox: false,
            destinationBoxRight: 0,
            destinationBoxBottom: 0,
            destinationBoxBack: 0,
            rowPitch: storageRowSize,
            depthPitch: storageRowSize * storageRowCount,
            format: format);
        AssertPlaybackTrace(
            info,
            traces[1],
            expectedDelta: 24,
            destinationX: patchX,
            destinationY: patchY,
            destinationZ: 0,
            hasDestinationBox: true,
            destinationBoxRight: 12,
            destinationBoxBottom: 12,
            destinationBoxBack: 1,
            rowPitch: patchStorageRowSize,
            depthPitch: patchStorageRowSize * patchStorageRowCount,
            format: format);

        MappedResource mapped = GD.Map(capture, MapMode.Read);
        try
        {
            byte* basePointer = (byte*)mapped.Data;
            for (uint row = 0; row < storageRowCount; row++)
            {
                for (uint column = 0; column < storageRowSize; column++)
                {
                    byte actual = *(basePointer + checked((nint)(
                        (row * mapped.RowPitch) + column)));
                    Assert.Equal(
                        expected[checked((int)(row * storageRowSize + column))],
                        actual);
                }
            }
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    private BackendInfoD3D11 GetForcedFallbackInfo()
    {
        Assert.True(GD.GetD3D11Info(out BackendInfoD3D11 info));
        Assert.Equal(
            D3D11DeferredTextureUploadMode.ForceSoftwareCommandListEmulation,
            info.DeferredTextureUploadMode);
        Assert.True(info.UsesSoftwareCommandListTextureUploadPath);
        Assert.True(info.IsSoftwareCommandListTextureUploadPathForced);
        Assert.Equal(
            D3D11DeferredTextureUploadPlaybackPath
                .ForcedSoftwareCommandListRuntime,
            info.DeferredTextureUploadPlaybackPath);
        Assert.Equal(
            info.DriverCommandListsSupported,
            info.UsesNativeSoftwareRuntimePlaybackTranslation);
        return info;
    }

    private static D3D11DeferredTextureUploadPlaybackTrace[]
        GetNewPlaybackTraces(
            BackendInfoD3D11 info,
            int traceCountBefore,
            int expectedCount)
    {
        D3D11DeferredTextureUploadPlaybackTrace[] allTraces =
            info.GetDeferredTextureUploadPlaybackTraces();
        Assert.Equal(traceCountBefore + expectedCount, allTraces.Length);
        var newTraces =
            new D3D11DeferredTextureUploadPlaybackTrace[expectedCount];
        Array.Copy(
            allTraces,
            traceCountBefore,
            newTraces,
            0,
            expectedCount);
        return newTraces;
    }

    private static void AssertPlaybackTrace(
        BackendInfoD3D11 info,
        D3D11DeferredTextureUploadPlaybackTrace trace,
        ulong expectedDelta,
        uint destinationX,
        uint destinationY,
        uint destinationZ,
        bool hasDestinationBox,
        uint destinationBoxRight,
        uint destinationBoxBottom,
        uint destinationBoxBack,
        uint rowPitch,
        uint depthPitch,
        PixelFormat format)
    {
        Assert.Equal(
            D3D11DeferredTextureUploadPlaybackPath
                .ForcedSoftwareCommandListRuntime,
            trace.PlaybackPath);
        Assert.Equal(expectedDelta, trace.LogicalSourceMinusRebasedSource);
        Assert.Equal(destinationX, trace.DestinationX);
        Assert.Equal(destinationY, trace.DestinationY);
        Assert.Equal(destinationZ, trace.DestinationZ);
        Assert.Equal(hasDestinationBox, trace.HasDestinationBox);
        Assert.Equal(destinationBoxRight, trace.DestinationBoxRight);
        Assert.Equal(destinationBoxBottom, trace.DestinationBoxBottom);
        Assert.Equal(destinationBoxBack, trace.DestinationBoxBack);
        Assert.Equal(rowPitch, trace.RowPitch);
        Assert.Equal(depthPitch, trace.DepthPitch);
        Assert.Equal(format, trace.Format);
        Assert.Equal(
            info.DriverCommandListsSupported,
            trace.NativePlaybackTranslationApplied);
    }

    private void AssertR8TextureEquals(
        Texture texture,
        byte[] expected,
        uint width,
        uint height,
        uint depth)
    {
        MappedResource mapped = GD.Map(texture, MapMode.Read);
        try
        {
            byte* basePointer = (byte*)mapped.Data;
            for (uint z = 0; z < depth; z++)
            {
                for (uint y = 0; y < height; y++)
                {
                    for (uint x = 0; x < width; x++)
                    {
                        byte actual = *(basePointer + checked((nint)(
                            (z * mapped.DepthPitch) +
                            (y * mapped.RowPitch) +
                            x)));
                        Assert.Equal(
                            expected[checked((int)(
                                (z * height * width) +
                                (y * width) +
                                x))],
                            actual);
                    }
                }
            }
        }
        finally
        {
            GD.Unmap(texture);
        }
    }
}
#endif
