using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
#if TEST_D3D11
using NeoVeldrid.D3D11;
#endif
#if TEST_OPENGL || TEST_OPENGLES
using NeoVeldrid.OpenGL;
#endif
#if TEST_VULKAN
using NeoVeldrid.Vk;
#endif
using Xunit;

namespace NeoVeldrid.Tests;

// Regression tests for specific bugs that have been fixed in NeoVeldrid. Each test in this
// file exists to prevent a particular past bug from coming back; they are not part of the
// general behavioral coverage in TextureTests.cs and should not be relied on as
// documentation of the public contract. The test name and a comment above each test should
// describe the bug it guards against.
//
// This is a partial of TextureTestBase<T>, so any test added here is automatically picked
// up by every per-backend concrete subclass declared at the bottom of TextureTests.cs
// (VulkanTextureTests, D3D11TextureTests, OpenGLTextureTests, OpenGLESTextureTests).
public abstract partial class TextureTestBase<T> where T : GraphicsDeviceCreator
{
    // Regression test for a Vulkan backend bug where R16_G16_Float and R32_G32_Float were
    // mapped to their 4-component VK_FORMAT_..._SFLOAT counterparts, doubling the per-pixel
    // byte size of every texture in those formats. Pure upload-then-readback round-trips
    // could hide the bug because the wrong stride was applied symmetrically on both copies.
    // Clearing the render target exposes it: the clear color carries B and A components
    // that should be discarded for a 2-component target, so any leakage of B/A into the
    // readback proves the underlying texture has the wrong number of components.
    [Fact]
    public void ClearColorTarget_R32_G32_Float_OnlyWritesTwoComponents()
    {
        const uint width = 4;
        const uint height = 1;

        Texture target = RF.CreateTexture(TextureDescription.Texture2D(
            width, height, 1, 1, PixelFormat.R32_G32_Float, TextureUsage.RenderTarget));
        Framebuffer fb = RF.CreateFramebuffer(new FramebufferDescription(null, target));

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.SetFramebuffer(fb);
        // 999f and 7777f are deliberately distinctive sentinels for the B and A channels.
        // The target is R32_G32_Float, so by the contract those two values must be ignored
        // and every pixel must read back as exactly (3, 5). The sentinels ensure that any
        // accidental leakage of B/A into the readback is loud and unambiguous.
        cl.ClearColorTarget(0, new RgbaFloat(3f, 5f, 999f, 7777f));
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        Texture staging = RF.CreateTexture(TextureDescription.Texture2D(
            width, height, 1, 1, PixelFormat.R32_G32_Float, TextureUsage.Staging));
        cl.Begin();
        cl.CopyTexture(target, 0, 0, 0, 0, 0, staging, 0, 0, 0, 0, 0, width, height, 1, 1);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        MappedResourceView<Vector2> view = GD.Map<Vector2>(staging, MapMode.Read);
        try
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal(new Vector2(3f, 5f), view[x, y]);
                }
            }
        }
        finally
        {
            GD.Unmap(staging);
        }

        cl.Dispose();
        fb.Dispose();
        staging.Dispose();
        target.Dispose();
    }

    [Fact]
    public void ClearColorTarget_R16_G16_Float_OnlyWritesTwoComponents()
    {
        const uint width = 4;
        const uint height = 1;

        Texture target = RF.CreateTexture(TextureDescription.Texture2D(
            width, height, 1, 1, PixelFormat.R16_G16_Float, TextureUsage.RenderTarget));
        Framebuffer fb = RF.CreateFramebuffer(new FramebufferDescription(null, target));

        CommandList cl = RF.CreateCommandList();
        cl.Begin();
        cl.SetFramebuffer(fb);
        // 999f and 7777f are deliberately distinctive sentinels for the B and A channels:
        // the target is R16_G16_Float, so by the contract those two values must be ignored
        // and every pixel must read back as exactly (1, 2). All four values are exactly
        // representable in IEEE-754 binary16, so the float32 -> float16 narrowing the clear
        // path performs is lossless and would faithfully preserve the sentinels if a bug
        // ever let them leak into the readback.
        cl.ClearColorTarget(0, new RgbaFloat(1f, 2f, 999f, 7777f));
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        Texture staging = RF.CreateTexture(TextureDescription.Texture2D(
            width, height, 1, 1, PixelFormat.R16_G16_Float, TextureUsage.Staging));
        cl.Begin();
        cl.CopyTexture(target, 0, 0, 0, 0, 0, staging, 0, 0, 0, 0, 0, width, height, 1, 1);
        cl.End();
        GD.SubmitCommands(cl);
        GD.WaitForIdle();

        MappedResourceView<HalfVector2> view = GD.Map<HalfVector2>(staging, MapMode.Read);
        try
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal((Half)1f, view[x, y].X);
                    Assert.Equal((Half)2f, view[x, y].Y);
                }
            }
        }
        finally
        {
            GD.Unmap(staging);
        }

        cl.Dispose();
        fb.Dispose();
        staging.Dispose();
        target.Dispose();
    }

    // Regression test for command-list texture upload paths which accidentally
    // treated a 3D region like a 2D image. The sentinels on both sides of the
    // uploaded depth range make an incorrect base Z or depth immediately visible.
    [Fact]
    public void CommandListTextureUpdate_NonzeroZ_PreservesSurroundingVoxels()
    {
        const uint textureWidth = 5;
        const uint textureHeight = 4;
        const uint textureDepth = 5;
        const uint uploadX = 1;
        const uint uploadY = 1;
        const uint uploadZ = 1;
        const uint uploadWidth = 3;
        const uint uploadHeight = 2;
        const uint uploadDepth = 3;
        const byte sentinel = 0x2D;

        Texture destination = RF.CreateTexture(TextureDescription.Texture3D(
            textureWidth,
            textureHeight,
            textureDepth,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled));
        Texture capture = RF.CreateTexture(TextureDescription.Texture3D(
            textureWidth,
            textureHeight,
            textureDepth,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Staging));

        byte[] initial = new byte[checked((int)(textureWidth * textureHeight * textureDepth))];
        Array.Fill(initial, sentinel);
        GD.UpdateTexture(
            destination,
            initial,
            0,
            0,
            0,
            textureWidth,
            textureHeight,
            textureDepth,
            0,
            0);

        byte[] upload = new byte[checked((int)(uploadWidth * uploadHeight * uploadDepth))];
        for (int i = 0; i < upload.Length; i++)
            upload[i] = checked((byte)(0x80 + i));
        byte[] expectedUpload = (byte[])upload.Clone();

        CommandList commandList = RF.CreateCommandList();
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            upload,
            uploadX,
            uploadY,
            uploadZ,
            uploadWidth,
            uploadHeight,
            uploadDepth,
            0,
            0);
        Array.Fill(upload, (byte)0xEE);
        commandList.CopyTexture(destination, capture);
        commandList.End();

        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        MappedResourceView<byte> view = GD.Map<byte>(capture, MapMode.Read);
        try
        {
            for (uint z = 0; z < textureDepth; z++)
            {
                for (uint y = 0; y < textureHeight; y++)
                {
                    for (uint x = 0; x < textureWidth; x++)
                    {
                        bool insideUpload =
                            x >= uploadX && x < uploadX + uploadWidth
                            && y >= uploadY && y < uploadY + uploadHeight
                            && z >= uploadZ && z < uploadZ + uploadDepth;
                        byte expected = sentinel;
                        if (insideUpload)
                        {
                            uint localX = x - uploadX;
                            uint localY = y - uploadY;
                            uint localZ = z - uploadZ;
                            uint sourceIndex =
                                ((localZ * uploadHeight) + localY) * uploadWidth + localX;
                            expected = expectedUpload[checked((int)sourceIndex)];
                        }

                        Assert.Equal(expected, view[x, y, z]);
                    }
                }
            }
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    // The upload submission deliberately contains no copy, sample, or second
    // use of destination. Readback happens in a later submission so another
    // command cannot retain destination and hide a broken UpdateTexture path.
    [Fact]
    public void CommandListTextureUpdate_RetainsCpuPayloadWithoutAnotherDestinationUse()
    {
        const uint width = 7;
        const uint height = 5;

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
        byte[] upload = new byte[checked((int)(width * height))];
        for (int i = 0; i < upload.Length; i++)
            upload[i] = checked((byte)(17 + (i * 3)));
        byte[] expected = (byte[])upload.Clone();

        CommandList uploadCommands = RF.CreateCommandList();
        Fence uploadCompletion = RF.CreateFence(signaled: false);
        uploadCommands.Begin();
        uploadCommands.UpdateTexture(
            destination,
            upload,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        uploadCommands.End();

        Array.Fill(upload, (byte)0xCC);
        GD.SubmitCommands(uploadCommands, uploadCompletion);
        Assert.True(GD.WaitForFence(uploadCompletion, TimeSpan.FromSeconds(5)));
        GD.WaitForIdle();

        CommandList readbackCommands = RF.CreateCommandList();
        readbackCommands.Begin();
        readbackCommands.CopyTexture(destination, capture);
        readbackCommands.End();
        GD.SubmitCommands(readbackCommands);
        GD.WaitForIdle();

        MappedResourceView<byte> view = GD.Map<byte>(capture, MapMode.Read);
        try
        {
            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    Assert.Equal(expected[checked((int)(y * width + x))], view[x, y]);
                }
            }
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    // The first transaction contains UpdateTexture and no other destination
    // use. Each backend follows its real ownership model: Vulkan exposes the
    // explicit submission RefCounts, GL/GLES expose their queued entry-list
    // reference, and D3D11 exposes the native ID3D11CommandList COM-retention
    // interval. A fresh second transaction adds a pre-recorded staging capture
    // and proves the exact bytes while repeating the same in-flight disposal
    // protocol. Because the upload-only transaction remains independent, the
    // capture copy cannot mask a missing destination owner in the first oracle.
    [Fact]
    public void SubmittedTextureUpdate_ReleasesItsOnlyDestinationOwnershipExactlyOnce()
    {
        const uint width = 7;
        const uint height = 5;
        Texture destination = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled));
        byte[] upload = new byte[checked((int)(width * height))];
        for (int i = 0; i < upload.Length; i++)
            upload[i] = checked((byte)(29 + (i * 5)));

        CommandList commandList = RF.CreateCommandList();
        Fence completion = RF.CreateFence(signaled: false);

        switch (GD.BackendType)
        {
#if TEST_VULKAN
            case GraphicsBackend.Vulkan:
                AssertVulkanTextureUploadOwnership(
                    destination,
                    commandList,
                    completion,
                    upload,
                    width,
                    height);
                break;
#endif
#if TEST_D3D11
            case GraphicsBackend.Direct3D11:
                AssertD3D11TextureUploadOwnership(
                    destination,
                    commandList,
                    completion,
                    upload,
                    width,
                    height);
                break;
#endif
#if TEST_OPENGL || TEST_OPENGLES
            case GraphicsBackend.OpenGL:
            case GraphicsBackend.OpenGLES:
                AssertOpenGLTextureUploadOwnership(
                    destination,
                    commandList,
                    completion,
                    upload,
                    width,
                    height);
                break;
#endif
            default:
                throw new NotSupportedException(
                    $"No texture-upload ownership oracle is defined for {GD.BackendType}.");
        }

#if TEST_D3D11 || TEST_VULKAN || TEST_OPENGL || TEST_OPENGLES
        AssertTextureUploadPayloadSurvivesInFlightDisposal(width, height);
#endif
    }

    // Lifecycle diagnostics must never become part of the ownership protocol.
    // Both callbacks deliberately fail here; the backend must still complete
    // its submission, release every real owner, and only then let the test
    // observe the queued diagnostic failures at an explicit safe boundary.
    [Fact]
    public void TextureUploadObserverFailuresAreDeferredUntilOwnershipCleanupCompletes()
    {
        const uint width = 7;
        const uint height = 5;
        Texture destination = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled));
        byte[] upload = new byte[checked((int)(width * height))];
        for (int i = 0; i < upload.Length; i++)
            upload[i] = checked((byte)(43 + (i * 3)));

        CommandList commandList = RF.CreateCommandList();
        Fence completion = RF.CreateFence(signaled: false);
        var observer = new ThrowingTextureUploadLifecycleObserver();
        GD.CommandListTextureUploadLifecycleObserver = observer;
        try
        {
            RecordOnlyTextureUpload(
                commandList,
                destination,
                upload,
                width,
                height);
            GD.SubmitCommands(commandList, completion);

            DisposeTrackedResource(destination);
            DisposeTrackedResource(commandList);

            bool completed = GD.WaitForFence(
                completion,
                TimeSpan.FromSeconds(5));
            GD.WaitForIdle();

            // These assertions precede the explicit rethrow. A callback
            // exception therefore cannot strand either the submission or the
            // caller-released resources on any backend.
            Assert.True(completed);
            Assert.True(destination.IsDisposed);
            Assert.True(commandList.IsDisposed);
            AssertTextureUploadObserverFailureCleanup(
                destination,
                commandList);
            Assert.Equal(1, observer.AcquireCount);
            Assert.Equal(1, observer.ReleaseCount);

            AggregateException failure = Assert.Throws<AggregateException>(
                GD.ThrowIfTextureUploadLifecycleObserverFailed);
            Assert.Collection(
                failure.InnerExceptions,
                exception => Assert.Equal(
                    ThrowingTextureUploadLifecycleObserver.AcquireFailureMessage,
                    exception.Message),
                exception => Assert.Equal(
                    ThrowingTextureUploadLifecycleObserver.ReleaseFailureMessage,
                    exception.Message));

            // Reporting owns and drains the failure queue. Old observer
            // failures must not leak into a later validation boundary.
            GD.ThrowIfTextureUploadLifecycleObserverFailed();
        }
        finally
        {
            GD.CommandListTextureUploadLifecycleObserver = null;
            try
            {
                GD.ThrowIfTextureUploadLifecycleObserverFailed();
            }
            catch
            {
                // The callback failures are intentional. If an earlier test
                // assertion failed, drain them so fixture teardown is isolated
                // from this diagnostic seam.
            }
        }
    }

    private void AssertTextureUploadObserverFailureCleanup(
        Texture destination,
        CommandList commandList)
    {
        switch (GD.BackendType)
        {
#if TEST_VULKAN
            case GraphicsBackend.Vulkan:
                Assert.Equal(
                    0,
                    Assert.IsType<VkTexture>(destination)
                        .RefCount.CurrentCount);
                Assert.Equal(
                    0,
                    Assert.IsType<VkCommandList>(commandList)
                        .RefCount.CurrentCount);
                break;
#endif
#if TEST_D3D11
            case GraphicsBackend.Direct3D11:
                Assert.False(
                    Assert.IsType<D3D11CommandList>(commandList)
                        .HasDeviceCommandList);
                break;
#endif
#if TEST_OPENGL || TEST_OPENGLES
            case GraphicsBackend.OpenGL:
            case GraphicsBackend.OpenGLES:
                Assert.True(
                    Assert.IsType<OpenGLTexture>(destination)
                        .NativeResourcesDestroyed);
                break;
#endif
            default:
                throw new NotSupportedException(
                    $"No texture-upload observer cleanup oracle is defined for {GD.BackendType}.");
        }
    }

    private static void RecordOnlyTextureUpload(
        CommandList commandList,
        Texture destination,
        byte[] upload,
        uint width,
        uint height)
    {
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            upload,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        commandList.End();
    }

    private static void RecordTextureUploadAndReadback(
        CommandList commandList,
        Texture destination,
        Texture capture,
        byte[] upload,
        uint width,
        uint height)
    {
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            upload,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        commandList.CopyTexture(destination, capture);
        commandList.End();
    }

    private static void RecordTextureUploadOwnershipTransaction(
        CommandList commandList,
        Texture destination,
        Texture capture,
        byte[] upload,
        uint width,
        uint height)
    {
        if (capture is null)
        {
            RecordOnlyTextureUpload(
                commandList,
                destination,
                upload,
                width,
                height);
            return;
        }

        RecordTextureUploadAndReadback(
            commandList,
            destination,
            capture,
            upload,
            width,
            height);
        Array.Fill(upload, (byte)0xCC);
    }

    private void AssertTextureUploadPayloadSurvivesInFlightDisposal(
        uint width,
        uint height)
    {
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
        Array.Fill(initial, (byte)0x12);
        GD.UpdateTexture(
            destination,
            initial,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        GD.WaitForIdle();

        byte[] upload = new byte[checked((int)(width * height))];
        for (int i = 0; i < upload.Length; i++)
            upload[i] = checked((byte)(71 + (i * 5)));
        byte[] expected = (byte[])upload.Clone();

        CommandList commandList = RF.CreateCommandList();
        Fence completion = RF.CreateFence(signaled: false);

        switch (GD.BackendType)
        {
#if TEST_VULKAN
            case GraphicsBackend.Vulkan:
                AssertVulkanTextureUploadOwnership(
                    destination,
                    commandList,
                    completion,
                    upload,
                    width,
                    height,
                    capture,
                    expected);
                break;
#endif
#if TEST_D3D11
            case GraphicsBackend.Direct3D11:
                AssertD3D11TextureUploadOwnership(
                    destination,
                    commandList,
                    completion,
                    upload,
                    width,
                    height,
                    capture,
                    expected);
                break;
#endif
#if TEST_OPENGL || TEST_OPENGLES
            case GraphicsBackend.OpenGL:
            case GraphicsBackend.OpenGLES:
                AssertOpenGLTextureUploadOwnership(
                    destination,
                    commandList,
                    completion,
                    upload,
                    width,
                    height,
                    capture,
                    expected);
                break;
#endif
            default:
                throw new NotSupportedException(
                    $"No texture-upload payload oracle is defined for {GD.BackendType}.");
        }
    }

    private void AssertTextureUploadReadback(
        Texture capture,
        byte[] expected,
        uint width,
        uint height)
    {
        MappedResourceView<byte> view = GD.Map<byte>(capture, MapMode.Read);
        try
        {
            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    Assert.Equal(
                        expected[checked((int)(y * width + x))],
                        view[x, y]);
                }
            }
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

#if TEST_VULKAN
    private void AssertVulkanTextureUploadOwnership(
        Texture destination,
        CommandList commandList,
        Fence completion,
        byte[] upload,
        uint width,
        uint height,
        Texture capture = null,
        byte[] expected = null)
    {
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        VkTexture vkDestination = Assert.IsType<VkTexture>(destination);
        VkCommandList vkCommandList = Assert.IsType<VkCommandList>(commandList);
        var probe = new TextureUploadLifecycleProbe(
            commandList,
            destination,
            blockOnAcquire: false);
        GD.CommandListTextureUploadLifecycleObserver = probe;
        try
        {
            // Sampled texture construction performs an ordered initial layout
            // transition. Drain that unrelated submission so every following
            // count belongs to this recording and submission only.
            GD.WaitForIdle();
            Assert.Equal(1, vkDestination.RefCount.CurrentCount);
            Assert.Equal(1, vkCommandList.RefCount.CurrentCount);

            RecordTextureUploadOwnershipTransaction(
                commandList,
                destination,
                capture,
                upload,
                width,
                height);
            Assert.Equal(2, vkDestination.RefCount.CurrentCount);
            Assert.Equal(1, vkCommandList.RefCount.CurrentCount);

            using var checkpoint = new BlockingVulkanSubmissionCheckpoint(
                VkGraphicsDevice.SubmissionCheckpoint.BeforeAuxiliaryCompletionSubmit);
            graphicsDevice.SubmissionCheckpointObserver = checkpoint;
            Task submission = Task.Run(
                () => GD.SubmitCommands(commandList, completion));
            bool resourcesDisposed = false;
            try
            {
                Assert.True(checkpoint.WaitUntilEntered(TimeSpan.FromSeconds(5)));

                // The primary native queue submit has succeeded and owns the
                // real destination and command-list references. The ordered
                // caller-fence marker has not yet been submitted, so holding
                // this checkpoint gives the test an admitted, deterministic
                // in-flight boundary.
                Assert.Equal(3, vkDestination.RefCount.CurrentCount);
                Assert.Equal(2, vkCommandList.RefCount.CurrentCount);
                Assert.Equal(1, probe.AcquireCount);
                Assert.Equal(0, probe.ReleaseCount);

                DisposeTrackedResource(destination);
                DisposeTrackedResource(commandList);
                resourcesDisposed = true;

                Assert.False(destination.IsDisposed);
                Assert.False(commandList.IsDisposed);
                Assert.Equal(2, vkDestination.RefCount.CurrentCount);
                Assert.Equal(1, vkCommandList.RefCount.CurrentCount);

                checkpoint.Release();
                submission.GetAwaiter().GetResult();
            }
            finally
            {
                checkpoint.Release();
                graphicsDevice.SubmissionCheckpointObserver = null;
                if (!submission.IsCompleted)
                    submission.GetAwaiter().GetResult();
            }

            Assert.True(resourcesDisposed);
            Assert.True(GD.WaitForFence(completion, TimeSpan.FromSeconds(5)));
            GD.WaitForIdle();

            Assert.True(destination.IsDisposed);
            Assert.True(commandList.IsDisposed);
            Assert.Equal(0, vkDestination.RefCount.CurrentCount);
            Assert.Equal(0, vkCommandList.RefCount.CurrentCount);
            Assert.Equal(1, probe.AcquireCount);
            Assert.Equal(1, probe.ReleaseCount);
            // The submission-resource owner has released at this callback;
            // the committed image-layout transaction remains until the
            // completed command-list recording is recycled immediately after.
            Assert.Equal(1, probe.RemainingBackendOwnershipCountAtRelease);
            probe.ThrowIfCallbackFailed();
            GD.ThrowIfTextureUploadLifecycleObserverFailed();
            if (capture is not null)
            {
                AssertTextureUploadReadback(
                    capture,
                    expected,
                    width,
                    height);
            }
        }
        finally
        {
            GD.CommandListTextureUploadLifecycleObserver = null;
            probe.Release();
            probe.Dispose();
        }
    }
#endif

#if TEST_D3D11
    private void AssertD3D11TextureUploadOwnership(
        Texture destination,
        CommandList commandList,
        Fence completion,
        byte[] upload,
        uint width,
        uint height,
        Texture capture = null,
        byte[] expected = null)
    {
        var probe = new TextureUploadLifecycleProbe(
            commandList,
            destination,
            blockOnAcquire: false);
        GD.CommandListTextureUploadLifecycleObserver = probe;
        try
        {
            RecordTextureUploadOwnershipTransaction(
                commandList,
                destination,
                capture,
                upload,
                width,
                height);

            D3D11CommandList d3d11CommandList =
                Assert.IsType<D3D11CommandList>(commandList);
            Assert.True(d3d11CommandList.HasDeviceCommandList);
            Assert.Equal(1, probe.AcquireCount);
            Assert.Equal(0, probe.ReleaseCount);

            // FinishCommandList has acquired the native command list's COM
            // references. Release the caller's destination reference before
            // ExecuteCommandList. In the first transaction the upload is the
            // only native use capable of keeping the destination valid; the
            // independent capture transaction repeats this disposal timing
            // without weakening that earlier ownership-only oracle.
            DisposeTrackedResource(destination);
            Assert.True(destination.IsDisposed);

            // D3D11 transfers the native command list synchronously to the
            // immediate context. OnCompleted releases the command-list COM
            // ownership at that deterministic handoff boundary; the driver
            // owns any still-executing native work until the fence completes.
            GD.SubmitCommands(commandList, completion);
            Assert.False(d3d11CommandList.HasDeviceCommandList);
            Assert.Equal(1, probe.AcquireCount);
            Assert.Equal(1, probe.ReleaseCount);

            // Submission is synchronous only through the immediate-context
            // handoff. Retire the managed wrapper now, while the caller fence
            // still represents potentially in-flight native work.
            DisposeTrackedResource(commandList);
            Assert.True(commandList.IsDisposed);
            Assert.Equal(1, probe.ReleaseCount);
            Assert.Equal(0, probe.RemainingBackendOwnershipCountAtRelease);

            Assert.True(GD.WaitForFence(completion, TimeSpan.FromSeconds(5)));
            GD.WaitForIdle();
            GD.CheckValidation(
                "D3D11 texture-upload destination ownership release");

            probe.ThrowIfCallbackFailed();
            GD.ThrowIfTextureUploadLifecycleObserverFailed();
            if (capture is not null)
            {
                AssertTextureUploadReadback(
                    capture,
                    expected,
                    width,
                    height);
            }
        }
        finally
        {
            GD.CommandListTextureUploadLifecycleObserver = null;
            probe.Release();
            probe.Dispose();
        }
    }
#endif

#if TEST_OPENGL || TEST_OPENGLES
    private void AssertOpenGLTextureUploadOwnership(
        Texture destination,
        CommandList commandList,
        Fence completion,
        byte[] upload,
        uint width,
        uint height,
        Texture capture = null,
        byte[] expected = null)
    {
        OpenGLTexture glDestination = Assert.IsType<OpenGLTexture>(destination);
        Assert.IsType<OpenGLCommandList>(commandList);
        var probe = new TextureUploadLifecycleProbe(
            commandList,
            destination,
            blockOnAcquire: true);
        GD.CommandListTextureUploadLifecycleObserver = probe;
        Task submission = null;
        bool submissionAwaited = false;
        bool resourcesDisposed = false;
        List<Exception> failures = null;
        try
        {
            try
            {
                RecordTextureUploadOwnershipTransaction(
                    commandList,
                    destination,
                    capture,
                    upload,
                    width,
                    height);

                submission = Task.Run(
                    () => GD.SubmitCommands(commandList, completion));
                Assert.True(probe.WaitUntilAcquired(TimeSpan.FromSeconds(5)));
                Assert.Equal(1, probe.AcquireCount);
                Assert.Equal(0, probe.ReleaseCount);
                Assert.False(completion.Signaled);

                // The real GL work queue has admitted this submission. In a
                // validation boundary the entry list may execute inline inside
                // that already-admitted work item; either way, the admission
                // handshake prevents entry-list execution until this observer
                // returns. The entry list still owns the tracked destination,
                // and command-list disposal is deferred behind the submission.
                DisposeTrackedResource(destination);
                DisposeTrackedResource(commandList);
                resourcesDisposed = true;
                Assert.True(destination.IsDisposed);
                Assert.False(glDestination.NativeResourcesDestroyed);
                Assert.False(commandList.IsDisposed);

                probe.Release();
                try
                {
                    submission.GetAwaiter().GetResult();
                }
                finally
                {
                    // A faulted task has still been observed; the outer
                    // cleanup only needs to await submissions bypassed by an
                    // earlier assertion.
                    submissionAwaited = true;
                }

                Assert.True(resourcesDisposed);
                Assert.True(
                    GD.WaitForFence(completion, TimeSpan.FromSeconds(5)));
                GD.WaitForIdle();
                Assert.Equal(1, probe.AcquireCount);
                Assert.Equal(1, probe.ReleaseCount);
                Assert.Equal(
                    0,
                    probe.RemainingBackendOwnershipCountAtRelease);
                Assert.True(glDestination.NativeResourcesDestroyed);
                Assert.True(commandList.IsDisposed);
                probe.ThrowIfCallbackFailed();
                GD.ThrowIfTextureUploadLifecycleObserverFailed();
                if (capture is not null)
                {
                    AssertTextureUploadReadback(
                        capture,
                        expected,
                        width,
                        height);
                }
            }
            catch (Exception exception)
            {
                AddTextureUploadOwnershipFailure(exception, ref failures);
            }
        }
        finally
        {
            probe.Release();

            if (submission is not null && !submissionAwaited)
            {
                CaptureTextureUploadOwnershipFailure(
                    () => submission.GetAwaiter().GetResult(),
                    ref failures);
            }

            if (submission is not null)
            {
                CaptureTextureUploadOwnershipFailure(
                    () =>
                    {
                        if (!GD.WaitForFence(
                            completion,
                            TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException(
                                "The OpenGL texture-upload fence did not complete during lifecycle cleanup.");
                        }
                    },
                    ref failures);
            }

            CaptureTextureUploadOwnershipFailure(
                GD.WaitForIdle,
                ref failures);
            CaptureTextureUploadOwnershipFailure(
                probe.ThrowIfCallbackFailed,
                ref failures);
            CaptureTextureUploadOwnershipFailure(
                GD.ThrowIfTextureUploadLifecycleObserverFailed,
                ref failures);
            GD.CommandListTextureUploadLifecycleObserver = null;
            probe.Dispose();
        }

        ThrowTextureUploadOwnershipFailures(failures);
    }
#endif

    private sealed class ThrowingTextureUploadLifecycleObserver
        : ICommandListTextureUploadLifecycleObserver
    {
        public const string AcquireFailureMessage =
            "Intentional texture-upload retention-acquire observer failure.";
        public const string ReleaseFailureMessage =
            "Intentional texture-upload retention-release observer failure.";

        private int _acquireCount;
        private int _releaseCount;

        public int AcquireCount => Volatile.Read(ref _acquireCount);

        public int ReleaseCount => Volatile.Read(ref _releaseCount);

        public void OnRetentionAcquired(
            CommandList commandList,
            Texture destination)
        {
            Interlocked.Increment(ref _acquireCount);
            throw new InvalidOperationException(AcquireFailureMessage);
        }

        public void OnRetentionReleased(
            CommandList commandList,
            int remainingBackendOwnershipCount)
        {
            Interlocked.Increment(ref _releaseCount);
            throw new InvalidOperationException(ReleaseFailureMessage);
        }
    }

    private sealed class TextureUploadLifecycleProbe
        : ICommandListTextureUploadLifecycleObserver,
          IDisposable
    {
        private readonly WeakReference<CommandList> _expectedCommandList;
        private readonly WeakReference<Texture> _expectedDestination;
        private readonly bool _blockOnAcquire;
        private readonly ManualResetEventSlim _acquired = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly object _failureLock = new();
        private List<Exception> _callbackFailures;
        private int _acquireCount;
        private int _releaseCount;
        private int _remainingBackendOwnershipCountAtRelease = -1;

        public TextureUploadLifecycleProbe(
            CommandList expectedCommandList,
            Texture expectedDestination,
            bool blockOnAcquire)
        {
            _expectedCommandList = new WeakReference<CommandList>(
                expectedCommandList);
            _expectedDestination = new WeakReference<Texture>(
                expectedDestination);
            _blockOnAcquire = blockOnAcquire;
        }

        public int AcquireCount => Volatile.Read(ref _acquireCount);

        public int ReleaseCount => Volatile.Read(ref _releaseCount);

        public int RemainingBackendOwnershipCountAtRelease =>
            Volatile.Read(
                ref _remainingBackendOwnershipCountAtRelease);

        public void OnRetentionAcquired(
            CommandList commandList,
            Texture destination)
        {
            try
            {
                EnsureExpected(commandList, destination);
                Interlocked.Increment(ref _acquireCount);
                _acquired.Set();
                if (_blockOnAcquire
                    && !_release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Texture-upload lifecycle probe was not released.");
                }
            }
            catch (Exception exception)
            {
                RecordCallbackFailure(exception);
            }
        }

        public void OnRetentionReleased(
            CommandList commandList,
            int remainingBackendOwnershipCount)
        {
            try
            {
                EnsureExpectedCommandList(commandList);
                if (remainingBackendOwnershipCount < 0)
                {
                    throw new InvalidOperationException(
                        $"The backend reported an invalid remaining texture-upload ownership count of {remainingBackendOwnershipCount}.");
                }

                Interlocked.Exchange(
                    ref _remainingBackendOwnershipCountAtRelease,
                    remainingBackendOwnershipCount);
                Interlocked.Increment(ref _releaseCount);
            }
            catch (Exception exception)
            {
                RecordCallbackFailure(exception);
            }
        }

        public bool WaitUntilAcquired(TimeSpan timeout) =>
            _acquired.Wait(timeout);

        public void Release() => _release.Set();

        public void ThrowIfCallbackFailed()
        {
            Exception[] failures;
            lock (_failureLock)
            {
                if (_callbackFailures is null
                    || _callbackFailures.Count == 0)
                {
                    return;
                }

                failures = _callbackFailures.ToArray();
                _callbackFailures.Clear();
            }

            if (failures.Length == 1)
                ExceptionDispatchInfo.Capture(failures[0]).Throw();

            throw new AggregateException(
                "Texture-upload lifecycle probe callbacks failed.",
                failures);
        }

        public void Dispose()
        {
            _acquired.Dispose();
            _release.Dispose();
        }

        private void EnsureExpected(
            CommandList commandList,
            Texture destination)
        {
            EnsureExpectedCommandList(commandList);
            if (!_expectedDestination.TryGetTarget(out Texture expected)
                || !ReferenceEquals(expected, destination))
            {
                throw new InvalidOperationException(
                    "A texture-upload lifecycle event described an unexpected resource.");
            }
        }

        private void EnsureExpectedCommandList(CommandList commandList)
        {
            if (!_expectedCommandList.TryGetTarget(out CommandList expected)
                || !ReferenceEquals(expected, commandList))
            {
                throw new InvalidOperationException(
                    "A texture-upload lifecycle event described an unexpected command list.");
            }
        }

        private void RecordCallbackFailure(Exception exception)
        {
            lock (_failureLock)
            {
                (_callbackFailures ??= new List<Exception>()).Add(exception);
            }
        }
    }

    private static void CaptureTextureUploadOwnershipFailure(
        Action action,
        ref List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            AddTextureUploadOwnershipFailure(exception, ref failures);
        }
    }

    private static void AddTextureUploadOwnershipFailure(
        Exception exception,
        ref List<Exception> failures) =>
        (failures ??= new List<Exception>()).Add(exception);

    private static void ThrowTextureUploadOwnershipFailures(
        List<Exception> failures)
    {
        if (failures is null || failures.Count == 0)
            return;

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();

        throw new AggregateException(
            "Texture-upload ownership validation encountered multiple failures.",
            failures);
    }

#if TEST_VULKAN
    private sealed class BlockingVulkanSubmissionCheckpoint
        : VkGraphicsDevice.ISubmissionCheckpointObserver,
          IDisposable
    {
        private readonly VkGraphicsDevice.SubmissionCheckpoint _checkpoint;
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public BlockingVulkanSubmissionCheckpoint(
            VkGraphicsDevice.SubmissionCheckpoint checkpoint)
        {
            _checkpoint = checkpoint;
        }

        public void OnCheckpoint(
            VkGraphicsDevice.SubmissionCheckpoint checkpoint)
        {
            if (checkpoint != _checkpoint)
                return;

            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "Vulkan submission checkpoint was not released.");
            }
        }

        public bool WaitUntilEntered(TimeSpan timeout) =>
            _entered.Wait(timeout);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _entered.Dispose();
            _release.Dispose();
        }
    }
#endif

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct HalfVector2
    {
        public Half X;
        public Half Y;
    }
}
