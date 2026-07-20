#if TEST_VULKAN
using System;
using System.Collections.Generic;
using System.Reflection;
using NeoVeldrid.Vk;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Sdk;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanStagedUploadIsolationTests
    : GraphicsDeviceTestBase<VulkanDeviceCreator>
{
    private const int BoundedSubmissionCapacity = 8;
    private const int SubmissionCount = BoundedSubmissionCapacity * 3;
    private const int RegionCount = 16;

    [Theory]
    [InlineData(PixelFormat.R8_UNorm, 1u)]
    [InlineData(PixelFormat.R8_G8_B8_A8_UNorm, 4u)]
    [InlineData(PixelFormat.BC1_Rgba_UNorm, 8u)]
    [InlineData(PixelFormat.BC3_UNorm, 16u)]
    public void TextureUploadAlignmentFollowsTexelBlockSize(
        PixelFormat format,
        uint expectedAlignment)
    {
        Assert.Equal(
            expectedAlignment,
            VkTextureUploadRecorder.GetRequiredStagingAlignment(format));
    }

    [Fact]
    public void AbandonedEndedTextureUploadRestoresOriginalLayout()
    {
        const uint size = 4;
        Texture texture = RF.CreateTexture(
            TextureDescription.Texture2D(
                size,
                size,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Storage));
        VkTexture vkTexture = Assert.IsType<VkTexture>(texture);
        byte[] first = new byte[checked((int)(size * size))];
        byte[] second = new byte[first.Length];
        Array.Fill(first, (byte)17);
        Array.Fill(second, (byte)93);
        CommandList commandList = RF.CreateCommandList();

        Assert.Equal(ImageLayout.Undefined, vkTexture.GetImageLayout(0, 0));

        commandList.Begin();
        commandList.UpdateTexture(
            texture, first,
            0, 0, 0,
            size, size, 1,
            0, 0);
        commandList.End();
        Assert.Equal(ImageLayout.General, vkTexture.GetImageLayout(0, 0));

        commandList.Begin();
        Assert.Equal(ImageLayout.Undefined, vkTexture.GetImageLayout(0, 0));
        commandList.UpdateTexture(
            texture, second,
            0, 0, 0,
            size, size, 1,
            0, 0);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.Equal(ImageLayout.General, vkTexture.GetImageLayout(0, 0));
    }

    [Fact]
    public void SharedTextureCommandListsMustSubmitInRecordingOrder()
    {
        const uint size = 4;
        Texture texture = RF.CreateTexture(
            TextureDescription.Texture2D(
                size,
                size,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Storage));
        byte[] first = new byte[checked((int)(size * size))];
        byte[] second = new byte[first.Length];
        Array.Fill(first, (byte)11);
        Array.Fill(second, (byte)29);
        CommandList firstCommandList = RF.CreateCommandList();
        CommandList secondCommandList = RF.CreateCommandList();

        firstCommandList.Begin();
        firstCommandList.UpdateTexture(
            texture, first,
            0, 0, 0,
            size, size, 1,
            0, 0);
        firstCommandList.End();

        secondCommandList.Begin();
        secondCommandList.UpdateTexture(
            texture, second,
            0, 0, 0,
            size, size, 1,
            0, 0);
        secondCommandList.End();

        NeoVeldridException error = Assert.Throws<NeoVeldridException>(
            () => GD.SubmitCommands(secondCommandList));
        Assert.Contains("recording order", error.Message);

        GD.SubmitCommands(firstCommandList);
        GD.SubmitCommands(secondCommandList);
        GD.WaitForIdle();
    }

    [Fact]
    public void OppositeOrderMultiTextureRecordingIsRejectedBeforeDependencyCycle()
    {
        const uint size = 4;
        Texture firstTexture = RF.CreateTexture(
            TextureDescription.Texture2D(
                size,
                size,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Storage));
        Texture secondTexture = RF.CreateTexture(
            TextureDescription.Texture2D(
                size,
                size,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Storage));
        byte[] pixels = new byte[checked((int)(size * size))];
        CommandList firstCommandList = RF.CreateCommandList();
        CommandList secondCommandList = RF.CreateCommandList();

        firstCommandList.Begin();
        firstCommandList.UpdateTexture(
            firstTexture, pixels,
            0, 0, 0,
            size, size, 1,
            0, 0);

        secondCommandList.Begin();
        secondCommandList.UpdateTexture(
            secondTexture, pixels,
            0, 0, 0,
            size, size, 1,
            0, 0);

        NeoVeldridException error = Assert.Throws<NeoVeldridException>(
            () => firstCommandList.UpdateTexture(
                secondTexture, pixels,
                0, 0, 0,
                size, size, 1,
                0, 0));
        Assert.Contains("cyclic submission dependencies", error.Message);

        // The rejected edge was never registered, so both independent
        // recordings remain submitable and recyclable.
        firstCommandList.End();
        secondCommandList.End();
        GD.SubmitCommands(firstCommandList);
        GD.SubmitCommands(secondCommandList);
        GD.WaitForIdle();
    }

    [SkippableFact]
    public void StorageWriteTransitionsToExactRenderPassInitialLayout()
    {
        Skip.IfNot(
            GD.Features.ComputeShader,
            $"NV-SKIP-COMPUTE-SHADER: Compute shaders are unavailable on {GD.BackendType}.");

        const uint width = 4;
        const uint height = 1;
        Texture target = RF.CreateTexture(
            TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                PixelFormat.R32_G32_B32_A32_Float,
                TextureUsage.Storage | TextureUsage.RenderTarget));
        VkTexture vkTarget = Assert.IsType<VkTexture>(target);
        Framebuffer framebuffer = RF.CreateFramebuffer(
            new FramebufferDescription(null, target));
        ResourceLayout computeLayout = RF.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription(
                    "ComputeOutput",
                    ResourceKind.TextureReadWrite,
                    ShaderStages.Compute)));
        ResourceSet computeSet = RF.CreateResourceSet(
            new ResourceSetDescription(computeLayout, target));
        Pipeline computePipeline = RF.CreateComputePipeline(
            new ComputePipelineDescription(
                TestShaders.LoadCompute(RF, "ComputeTextureGenerator"),
                computeLayout,
                4,
                1,
                1));
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.SetPipeline(computePipeline);
        commandList.SetComputeResourceSet(0, computeSet);
        commandList.Dispatch(1, 1, 1);
        Assert.Equal(ImageLayout.General, vkTarget.GetImageLayout(0, 0));

        // End emits the framebuffer's otherwise-empty load pass. Its fixed
        // ColorAttachmentOptimal initial layout must match the explicit
        // transition from the preceding storage use.
        commandList.SetFramebuffer(framebuffer);
        commandList.End();
        Assert.Equal(
            ImageLayout.ColorAttachmentOptimal,
            vkTarget.GetImageLayout(0, 0));
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Texture readback = GetReadback(target);
        MappedResourceView<RgbaFloat> mapped =
            GD.Map<RgbaFloat>(readback, MapMode.Read);
        try
        {
            Assert.Equal(
                RgbaFloat.Red,
                mapped[0, 0],
                RgbaFloatFuzzyComparer.Instance);
            Assert.Equal(
                RgbaFloat.Green,
                mapped[1, 0],
                RgbaFloatFuzzyComparer.Instance);
            Assert.Equal(
                RgbaFloat.Blue,
                mapped[2, 0],
                RgbaFloatFuzzyComparer.Instance);
            Assert.Equal(
                RgbaFloat.White,
                mapped[3, 0],
                RgbaFloatFuzzyComparer.Instance);
        }
        finally
        {
            GD.Unmap(readback);
        }
    }

    [Theory]
    [InlineData(
        TextureUsage.Sampled | TextureUsage.Storage,
        ImageLayout.General)]
    [InlineData(
        TextureUsage.Sampled | TextureUsage.RenderTarget,
        ImageLayout.ColorAttachmentOptimal)]
    public void TextureUploadRestoresEstablishedPreTransferLayout(
        TextureUsage usage,
        ImageLayout establishedLayout)
    {
        const uint size = 4;
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        Texture texture = RF.CreateTexture(
            TextureDescription.Texture2D(
                size,
                size,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                usage));
        VkTexture vkTexture = Assert.IsType<VkTexture>(texture);
        graphicsDevice.TransitionImageLayout(vkTexture, establishedLayout);
        byte[] pixels = new byte[checked((int)(size * size * 4u))];
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.UpdateTexture(
            texture,
            pixels,
            0,
            0,
            0,
            size,
            size,
            1,
            0,
            0);

        Assert.Equal(establishedLayout, vkTexture.GetImageLayout(0, 0));

        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();
        Assert.Equal(establishedLayout, vkTexture.GetImageLayout(0, 0));
    }

    [Fact]
    public void RepeatedTransferOnlyTextureWritesPreserveLastPayload()
    {
        const uint width = 16;
        const uint height = 8;
        const int byteCount = (int)(width * height);
        Texture destination = RF.CreateTexture(
            TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                PixelFormat.R8_UNorm,
                (TextureUsage)0));
        Texture capture = RF.CreateTexture(
            TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Staging));
        byte[] first = new byte[byteCount];
        byte[] second = new byte[byteCount];
        Array.Fill(first, (byte)17);
        Array.Fill(second, (byte)93);
        CommandList commandList = RF.CreateCommandList(
            new CommandListDescription
            {
                MaximumInFlightSubmissionCount = 1,
                InitialTrackedResourceCapacityPerSubmission = 4,
                InitialStagingUploadPageSize = (uint)(byteCount * 2)
            });

        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            first,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        commandList.UpdateTexture(
            destination,
            second,
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
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        MappedResourceView<byte> mapped = GD.Map<byte>(capture, MapMode.Read);
        try
        {
            for (int i = 0; i < byteCount; i++)
                Assert.Equal((byte)93, mapped[(uint)i]);
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    [Fact]
    public void StorageTextureUploadRestoresGeneralLayout()
    {
        const uint size = 8;
        Texture texture = RF.CreateTexture(
            TextureDescription.Texture2D(
                size,
                size,
                1,
                1,
                PixelFormat.R32_Float,
                TextureUsage.Storage));
        VkTexture vkTexture = Assert.IsType<VkTexture>(texture);
        byte[] pixels = new byte[checked((int)(size * size * 4u))];
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.UpdateTexture(
            texture,
            pixels,
            0,
            0,
            0,
            size,
            size,
            1,
            0,
            0);
        Assert.Equal(ImageLayout.General, vkTexture.GetImageLayout(0, 0));
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();
    }

    [Fact]
    public void StorageTextureCopyRestoresGeneralLayouts()
    {
        const uint size = 8;
        Texture source = RF.CreateTexture(
            TextureDescription.Texture2D(
                size,
                size,
                1,
                1,
                PixelFormat.R32_Float,
                TextureUsage.Storage));
        Texture destination = RF.CreateTexture(
            TextureDescription.Texture2D(
                size,
                size,
                1,
                1,
                PixelFormat.R32_Float,
                TextureUsage.Storage));
        VkTexture vkSource = Assert.IsType<VkTexture>(source);
        VkTexture vkDestination = Assert.IsType<VkTexture>(destination);
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.CopyTexture(source, destination);
        Assert.Equal(ImageLayout.General, vkSource.GetImageLayout(0, 0));
        Assert.Equal(
            ImageLayout.General,
            vkDestination.GetImageLayout(0, 0));
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();
    }

    [Fact]
    public void RepeatedTextureCopiesToTransferOnlyDestinationPreserveLastPayload()
    {
        const uint width = 16;
        const uint height = 8;
        const int byteCount = (int)(width * height);
        Texture firstSource = RF.CreateTexture(
            TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Sampled));
        Texture secondSource = RF.CreateTexture(
            TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Sampled));
        Texture destination = RF.CreateTexture(
            TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                PixelFormat.R8_UNorm,
                (TextureUsage)0));
        Texture capture = RF.CreateTexture(
            TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.Staging));
        byte[] first = new byte[byteCount];
        byte[] second = new byte[byteCount];
        Array.Fill(first, (byte)31);
        Array.Fill(second, (byte)149);
        CommandList commandList = RF.CreateCommandList(
            new CommandListDescription
            {
                MaximumInFlightSubmissionCount = 1,
                InitialTrackedResourceCapacityPerSubmission = 8,
                InitialStagingUploadPageSize = (uint)(byteCount * 2)
            });

        commandList.Begin();
        commandList.UpdateTexture(
            firstSource,
            first,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        commandList.UpdateTexture(
            secondSource,
            second,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        commandList.CopyTexture(firstSource, destination);
        commandList.CopyTexture(secondSource, destination);
        commandList.CopyTexture(destination, capture);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        MappedResourceView<byte> mapped = GD.Map<byte>(capture, MapMode.Read);
        try
        {
            for (int i = 0; i < byteCount; i++)
                Assert.Equal((byte)149, mapped[(uint)i]);
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    [Fact]
    public void RetainedTextureUploadUsesPreallocatedPageAndFrameSubmission()
    {
        const uint width = 64;
        const uint height = 32;
        const uint byteCount = width * height;
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
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
        byte[] pixels = new byte[checked((int)byteCount)];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = unchecked((byte)(17 + (i * 29)));

        VkCommandList commandList = Assert.IsType<VkCommandList>(
            RF.CreateCommandList(new CommandListDescription
            {
                MaximumInFlightSubmissionCount = 1,
                InitialTrackedResourceCapacityPerSubmission = 8,
                InitialStagingUploadPageSize = byteCount
            }));
        commandList.EnableSubmissionDiagnostics(
            initialBufferAccessCapacity: 2);
        Assert.Equal(
            1,
            commandList
                .CaptureStagingResourcePoolSnapshot()
                .AvailableBufferCount);

        GD.WaitForIdle();
        int trackedBefore = graphicsDevice
            .CaptureSubmissionResourcePoolSnapshot()
            .TrackedSubmissionCount;
        commandList.Begin();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        commandList.UpdateTexture(
            destination,
            pixels,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        commandList.CopyTexture(destination, capture);
        commandList.End();

        VkCommandList.StagingResourcePoolSnapshot recordingPool =
            commandList.CaptureStagingResourcePoolSnapshot();
        Assert.Equal(0, recordingPool.AvailableBufferCount);
        Assert.Equal(1, recordingPool.CurrentBufferCount);
        Assert.InRange(allocated, 0L, 1024L);

        GD.SubmitCommands(commandList);
        Assert.True(commandList.TryGetLastSubmissionMetrics(
            out CommandListSubmissionMetrics submissionMetrics));
        Assert.Equal(1, submissionMetrics.UpdateTextureCallCount);
        Assert.Equal(byteCount, submissionMetrics.UpdatedTextureBytes);
        Assert.Equal(
            trackedBefore + 1,
            graphicsDevice
                .CaptureSubmissionResourcePoolSnapshot()
                .TrackedSubmissionCount);
        GD.WaitForIdle();

        MappedResourceView<byte> mapped = GD.Map<byte>(capture, MapMode.Read);
        try
        {
            for (uint i = 0; i < byteCount; i++)
                Assert.Equal(pixels[checked((int)i)], mapped[i]);
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    [Fact]
    public void BoundedSubmissionsPreserveEveryStagedUpdatePayload()
    {
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        int payloadSize = SumRegionSizes();
        DeviceBuffer shared = RF.CreateBuffer(new BufferDescription(
            checked((uint)payloadSize),
            BufferUsage.Staging));
        shared.Name = "staged-update-shared";

        DeviceBuffer[] captures = new DeviceBuffer[SubmissionCount];
        for (int submissionIndex = 0;
             submissionIndex < captures.Length;
             submissionIndex++)
        {
            captures[submissionIndex] = RF.CreateBuffer(new BufferDescription(
                checked((uint)payloadSize),
                BufferUsage.Staging));
            captures[submissionIndex].Name = $"staged-update-capture-{submissionIndex}";
        }

        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = BoundedSubmissionCapacity,
            InitialTrackedResourceCapacityPerSubmission = 32
        });
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: RegionCount + 1);
        uint uploadAlignment = GetPrivateUInt32Constant("BufferCopyAlignment");
        GD.WaitForIdle();
        using var reclamationGate =
            new VulkanAutomaticSubmissionReclamationGate(graphicsDevice);
        var retainedSlots = new HashSet<uint>();
        var retainedUploadPages = new HashSet<long>();
        uint oldestRetainedSlot = uint.MaxValue;
        long oldestRetainedUploadPage = 0;
        uint? alreadyBegunSubmissionSlot = null;

        for (int submissionIndex = 0;
             submissionIndex < captures.Length;
             submissionIndex++)
        {
            uint submissionSlot;
            if (alreadyBegunSubmissionSlot.HasValue)
            {
                submissionSlot = alreadyBegunSubmissionSlot.GetValueOrDefault();
                alreadyBegunSubmissionSlot = null;
            }
            else
            {
                commandList.Begin();
                submissionSlot = commandList.RecordingSubmissionSlot;
            }

            if (submissionIndex < BoundedSubmissionCapacity)
            {
                Assert.True(
                    retainedSlots.Add(submissionSlot),
                    $"Submission slot {submissionSlot} was reused before automatic reclamation was released.");
                if (submissionIndex == 0)
                    oldestRetainedSlot = submissionSlot;
            }

            uint destinationOffset = 0;
            for (int regionIndex = 0; regionIndex < RegionCount; regionIndex++)
            {
                int regionSize = regionIndex + 1;
                byte[] payload = CreateRegionPayload(
                    submissionIndex,
                    regionIndex,
                    regionSize);
                commandList.UpdateBuffer(shared, destinationOffset, payload);
                destinationOffset = checked(destinationOffset + (uint)payload.Length);
            }

            commandList.CopyBuffer(
                shared,
                0,
                captures[submissionIndex],
                0,
                checked((uint)payloadSize));
            commandList.End();

            GD.SubmitCommands(commandList);

            CommandListSubmissionSnapshot snapshot =
                commandList.SubmissionDiagnostics.CaptureLastSubmission();
            AssertSubmissionEvidence(
                snapshot,
                shared,
                captures[submissionIndex],
                payloadSize,
                uploadAlignment);

            if (submissionIndex < BoundedSubmissionCapacity)
            {
                long uploadPageIdentity =
                    snapshot.BufferAccesses[0].PrimaryResourceIdentity;
                Assert.True(
                    retainedUploadPages.Add(uploadPageIdentity),
                    $"Upload page {uploadPageIdentity} was reused while all bounded submissions were retained.");
                if (submissionIndex == 0)
                    oldestRetainedUploadPage = uploadPageIdentity;
            }
            else if (submissionIndex == BoundedSubmissionCapacity)
            {
                Assert.Equal(
                    oldestRetainedUploadPage,
                    snapshot.BufferAccesses[0].PrimaryResourceIdentity);
            }

            if (submissionIndex == BoundedSubmissionCapacity - 1)
            {
                Assert.Equal(BoundedSubmissionCapacity, retainedSlots.Count);
                Assert.Equal(
                    BoundedSubmissionCapacity,
                    retainedUploadPages.Count);
                Assert.Equal(
                    BoundedSubmissionCapacity,
                    graphicsDevice
                        .CaptureSubmissionResourcePoolSnapshot()
                        .TrackedSubmissionCount);

                VulkanSubmissionWraparoundObservation wraparound =
                    VulkanSubmissionWraparoundProbe.BeginAfterCapacityReached(
                        graphicsDevice,
                        commandList,
                        reclamationGate);
                Assert.True(
                    wraparound.WaitWasObserved,
                    "The capacity-plus-one Begin did not enter the bounded fence-wait path.");
                Assert.False(
                    wraparound.BeginCompletedWhilePaused,
                    "The capacity-plus-one Begin reused a retained upload owner before completion.");
                Assert.Equal(
                    BoundedSubmissionCapacity,
                    wraparound.TrackedSubmissionCountWhilePaused);
                Assert.Equal(
                    oldestRetainedSlot,
                    wraparound.RecordingSubmissionSlot);
                alreadyBegunSubmissionSlot =
                    wraparound.RecordingSubmissionSlot;
            }
        }

        GD.WaitForIdle();

        for (int submissionIndex = 0;
             submissionIndex < captures.Length;
             submissionIndex++)
        {
            MappedResourceView<byte> mapped = GD.Map<byte>(
                captures[submissionIndex],
                MapMode.Read);
            try
            {
                int payloadOffset = 0;
                for (int regionIndex = 0;
                     regionIndex < RegionCount;
                     regionIndex++)
                {
                    int regionSize = regionIndex + 1;
                    for (int byteIndex = 0;
                         byteIndex < regionSize;
                         byteIndex++)
                    {
                        Assert.Equal(
                            ExpectedRegionByte(
                                submissionIndex,
                                regionIndex,
                                byteIndex),
                            mapped[checked((uint)(payloadOffset + byteIndex))]);
                    }

                    payloadOffset += regionSize;
                }
            }
            finally
            {
                GD.Unmap(captures[submissionIndex]);
            }
        }
    }

    [Fact]
    public void UploadPageSuballocationSharesAndSpillsWithoutAliasing()
    {
        uint pageSize = GetPrivateUInt32Constant(
            "DefaultStagingUploadPageSize");
        uint alignment = GetPrivateUInt32Constant("BufferCopyAlignment");
        const int chunkSize = 257;
        uint alignedChunkSize = AlignUp(chunkSize, alignment);
        int chunkCount = checked((int)(pageSize / alignedChunkSize) + 1);
        int targetSize = checked(chunkCount * chunkSize);

        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            checked((uint)targetSize),
            BufferUsage.Staging));
        target.Name = "upload-page-suballocation-target";

        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 1,
            InitialTrackedResourceCapacityPerSubmission = 8
        });
        commandList.EnableSubmissionDiagnostics(
            initialBufferAccessCapacity: chunkCount);

        byte[] chunk = new byte[chunkSize];
        commandList.Begin();
        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            FillChunk(chunk, chunkIndex);
            commandList.UpdateBuffer(
                target,
                checked((uint)(chunkIndex * chunkSize)),
                chunk);
        }
        commandList.End();

        GD.SubmitCommands(commandList);

        CommandListSubmissionSnapshot snapshot =
            commandList.SubmissionDiagnostics.CaptureLastSubmission();
        Assert.Equal(chunkCount, snapshot.Metrics.UpdateBufferCallCount);
        Assert.Equal(
            checked((ulong)targetSize),
            snapshot.Metrics.UpdatedBufferBytes);
        Assert.Equal(chunkCount, snapshot.Metrics.CopyBufferCallCount);
        Assert.Equal(
            checked((ulong)targetSize),
            snapshot.Metrics.CopiedBufferBytes);
        Assert.Equal(chunkCount * 2, snapshot.Metrics.BufferAccessCount);
        AssertUploadPageSuballocationEvidence(
            snapshot,
            target,
            chunkCount,
            chunkSize,
            pageSize,
            alignment);

        GD.WaitForIdle();

        MappedResourceView<byte> mapped = GD.Map<byte>(target, MapMode.Read);
        try
        {
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                int chunkOffset = checked(chunkIndex * chunkSize);
                for (int byteIndex = 0; byteIndex < chunkSize; byteIndex++)
                {
                    Assert.Equal(
                        ExpectedChunkByte(chunkIndex, byteIndex),
                        mapped[checked((uint)(chunkOffset + byteIndex))]);
                }
            }
        }
        finally
        {
            GD.Unmap(target);
        }
    }

    [Fact]
    public void DeviceLocalDirectUpdateIsRetainedAndOrderedBeforeFollowingCopy()
    {
        const int byteCount = 4096;
        byte[] payload = new byte[byteCount];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = unchecked((byte)(13 + i * 37));

        DeviceBuffer deviceLocal = RF.CreateBuffer(new BufferDescription(
            byteCount,
            BufferUsage.VertexBuffer));
        deviceLocal.Name = "direct-update-device-local";
        DeviceBuffer capture = RF.CreateBuffer(new BufferDescription(
            byteCount,
            BufferUsage.Staging));

        GD.UpdateBuffer(deviceLocal, 0, payload);

        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 1,
            InitialTrackedResourceCapacityPerSubmission = 4
        });
        commandList.Begin();
        commandList.CopyBuffer(deviceLocal, 0, capture, 0, byteCount);
        commandList.End();
        GD.SubmitCommands(commandList);

        // Both the internal upload and the public command list must retain the
        // native target after its public ownership is released.
        deviceLocal.Dispose();
        GD.WaitForIdle();

        MappedResourceView<byte> mapped = GD.Map<byte>(capture, MapMode.Read);
        try
        {
            for (int i = 0; i < byteCount; i++)
                Assert.Equal(payload[i], mapped[checked((uint)i)]);
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    [Fact]
    public void ActiveHostMapRejectsSubmissionAndEndedCommandListCanBeRetried()
    {
        byte[] payload = { 3, 5, 8, 13, 21, 34, 55, 89 };
        DeviceBuffer mappedSource = RF.CreateBuffer(new BufferDescription(
            checked((uint)payload.Length),
            BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        mappedSource.Name = "mapped-submission-source";
        GD.UpdateBuffer(mappedSource, 0, payload);

        DeviceBuffer capture = RF.CreateBuffer(new BufferDescription(
            checked((uint)payload.Length),
            BufferUsage.Staging));
        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 1,
            InitialTrackedResourceCapacityPerSubmission = 4
        });
        commandList.Begin();
        commandList.CopyBuffer(
            mappedSource,
            0,
            capture,
            0,
            checked((uint)payload.Length));
        commandList.End();

        GD.Map(mappedSource, MapMode.Write);
        try
        {
            NeoVeldridException error = Assert.Throws<NeoVeldridException>(
                () => GD.SubmitCommands(commandList));
            Assert.Contains("host access is active", error.Message);
        }
        finally
        {
            GD.Unmap(mappedSource);
        }

        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        MappedResourceView<byte> mappedCapture = GD.Map<byte>(capture, MapMode.Read);
        try
        {
            for (int i = 0; i < payload.Length; i++)
                Assert.Equal(payload[i], mappedCapture[checked((uint)i)]);
        }
        finally
        {
            GD.Unmap(capture);
        }
    }

    [Fact]
    public void DisposedInFlightCommandListDisposesRecycledUploadPages()
    {
        byte[] payload = new byte[257];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = unchecked((byte)(i * 31 + 7));

        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            checked((uint)payload.Length),
            BufferUsage.VertexBuffer));
        VkCommandList commandList = Assert.IsType<VkCommandList>(
            RF.CreateCommandList(new CommandListDescription
            {
                MaximumInFlightSubmissionCount = 1,
                InitialTrackedResourceCapacityPerSubmission = 4
            }));

        commandList.Begin();
        commandList.UpdateBuffer(target, 0, payload);
        commandList.End();
        GD.SubmitCommands(commandList);

        // The public reference is released while the submission still owns the
        // command list. Completion must recycle the upload page before dropping
        // that final reference and running DisposeCore.
        commandList.Dispose();
        GD.WaitForIdle();

        Assert.True(commandList.IsDisposed);
        VkCommandList.StagingResourcePoolSnapshot snapshot =
            commandList.CaptureStagingResourcePoolSnapshot();
        Assert.Equal(1, snapshot.AvailableBufferCount);
        Assert.Equal(
            snapshot.AvailableBufferCount,
            snapshot.DisposedAvailableBufferCount);
        Assert.Equal(0, snapshot.CurrentBufferCount);
    }

    private static void AssertSubmissionEvidence(
        CommandListSubmissionSnapshot snapshot,
        DeviceBuffer shared,
        DeviceBuffer capture,
        int payloadSize,
        uint uploadAlignment)
    {
        CommandListSubmissionMetrics metrics = snapshot.Metrics;
        Assert.True(metrics.SubmissionSequence > 0);
        Assert.Equal(RegionCount, metrics.UpdateBufferCallCount);
        Assert.Equal(checked((ulong)payloadSize), metrics.UpdatedBufferBytes);
        Assert.Equal(RegionCount + 1, metrics.CopyBufferCallCount);
        Assert.Equal(
            checked((ulong)payloadSize * 2UL),
            metrics.CopiedBufferBytes);
        Assert.Equal(RegionCount * 2 + 1, metrics.BufferAccessCount);

        long sharedIdentity = 0;
        long uploadPageIdentity = 0;
        uint expectedOffset = 0;
        uint expectedUploadOffset = 0;
        for (int regionIndex = 0; regionIndex < RegionCount; regionIndex++)
        {
            int regionSize = regionIndex + 1;
            CommandListBufferAccess copy =
                snapshot.BufferAccesses[regionIndex * 2];
            CommandListBufferAccess update =
                snapshot.BufferAccesses[regionIndex * 2 + 1];

            Assert.Equal(
                CommandListBufferAccessKind.CopySourceAndDestination,
                copy.Kind);
            Assert.True(copy.HasSecondaryResource);
            Assert.Equal((ulong)expectedUploadOffset, copy.PrimaryOffsetInBytes);
            Assert.Equal((ulong)expectedOffset, copy.SecondaryOffsetInBytes);
            Assert.Equal((ulong)regionSize, copy.SizeInBytes);

            Assert.Equal(
                CommandListBufferAccessKind.UpdateDestination,
                update.Kind);
            Assert.Equal(shared.Name, update.PrimaryResourceName);
            Assert.Equal((ulong)expectedOffset, update.PrimaryOffsetInBytes);
            Assert.Equal((ulong)regionSize, update.SizeInBytes);
            if (regionIndex == 0)
            {
                sharedIdentity = update.PrimaryResourceIdentity;
                uploadPageIdentity = copy.PrimaryResourceIdentity;
            }
            else
            {
                Assert.Equal(sharedIdentity, update.PrimaryResourceIdentity);
                Assert.Equal(uploadPageIdentity, copy.PrimaryResourceIdentity);
            }

            Assert.Equal(sharedIdentity, copy.SecondaryResourceIdentity);

            expectedOffset = checked(expectedOffset + (uint)regionSize);
            expectedUploadOffset = AlignUp(
                checked(expectedUploadOffset + (uint)regionSize),
                uploadAlignment);
        }

        CommandListBufferAccess finalCopy =
            snapshot.BufferAccesses[RegionCount * 2];
        long expectedSharedIdentity =
            CommandListDiagnosticResourceIdentity.Get(shared);
        long expectedCaptureIdentity =
            CommandListDiagnosticResourceIdentity.Get(capture);
        Assert.Equal(expectedSharedIdentity, sharedIdentity);
        Assert.NotEqual(0L, uploadPageIdentity);
        Assert.NotEqual(expectedSharedIdentity, uploadPageIdentity);
        Assert.NotEqual(expectedCaptureIdentity, uploadPageIdentity);
        Assert.NotEqual(expectedSharedIdentity, expectedCaptureIdentity);
        Assert.Equal(
            CommandListBufferAccessKind.CopySourceAndDestination,
            finalCopy.Kind);
        Assert.Equal(expectedSharedIdentity, finalCopy.PrimaryResourceIdentity);
        Assert.True(finalCopy.HasSecondaryResource);
        Assert.Equal(expectedCaptureIdentity, finalCopy.SecondaryResourceIdentity);
        Assert.Equal(capture.Name, finalCopy.SecondaryResourceName);
        Assert.Equal(0UL, finalCopy.PrimaryOffsetInBytes);
        Assert.Equal(0UL, finalCopy.SecondaryOffsetInBytes);
        Assert.Equal(checked((ulong)payloadSize), finalCopy.SizeInBytes);
    }

    private static void AssertUploadPageSuballocationEvidence(
        CommandListSubmissionSnapshot snapshot,
        DeviceBuffer target,
        int chunkCount,
        int chunkSize,
        uint pageSize,
        uint alignment)
    {
        var uploadPageIdentities = new HashSet<long>();
        long targetIdentity = 0;
        long currentUploadPageIdentity = 0;
        uint expectedUploadOffset = 0;

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            uint alignedOffset = AlignUp(expectedUploadOffset, alignment);
            bool startsNewPage =
                alignedOffset > pageSize ||
                (uint)chunkSize > pageSize - alignedOffset;
            if (startsNewPage)
            {
                alignedOffset = 0;
                currentUploadPageIdentity = 0;
            }

            CommandListBufferAccess copy =
                snapshot.BufferAccesses[chunkIndex * 2];
            CommandListBufferAccess update =
                snapshot.BufferAccesses[chunkIndex * 2 + 1];
            Assert.Equal(
                CommandListBufferAccessKind.CopySourceAndDestination,
                copy.Kind);
            Assert.Equal(
                CommandListBufferAccessKind.UpdateDestination,
                update.Kind);

            Assert.Equal((ulong)alignedOffset, copy.PrimaryOffsetInBytes);
            Assert.Equal((ulong)chunkSize, copy.SizeInBytes);
            Assert.Equal(
                checked((ulong)(chunkIndex * chunkSize)),
                copy.SecondaryOffsetInBytes);
            Assert.Equal(
                checked((ulong)(chunkIndex * chunkSize)),
                update.PrimaryOffsetInBytes);
            Assert.Equal((ulong)chunkSize, update.SizeInBytes);
            Assert.Equal(target.Name, update.PrimaryResourceName);

            if (targetIdentity == 0)
                targetIdentity = update.PrimaryResourceIdentity;
            Assert.Equal(targetIdentity, update.PrimaryResourceIdentity);
            Assert.Equal(targetIdentity, copy.SecondaryResourceIdentity);

            if (currentUploadPageIdentity == 0)
                currentUploadPageIdentity = copy.PrimaryResourceIdentity;
            Assert.Equal(
                currentUploadPageIdentity,
                copy.PrimaryResourceIdentity);
            uploadPageIdentities.Add(copy.PrimaryResourceIdentity);

            expectedUploadOffset = checked(alignedOffset + (uint)chunkSize);
        }

        Assert.Equal(2, uploadPageIdentities.Count);
    }

    private static byte[] CreateRegionPayload(
        int submissionIndex,
        int regionIndex,
        int regionSize)
    {
        byte[] payload = new byte[regionSize];
        for (int byteIndex = 0; byteIndex < payload.Length; byteIndex++)
        {
            payload[byteIndex] = ExpectedRegionByte(
                submissionIndex,
                regionIndex,
                byteIndex);
        }

        return payload;
    }

    private static byte ExpectedRegionByte(
        int submissionIndex,
        int regionIndex,
        int byteIndex)
        => unchecked((byte)(
            17 +
            submissionIndex * 67 +
            regionIndex * 19 +
            byteIndex * 7));

    private static void FillChunk(Span<byte> destination, int chunkIndex)
    {
        for (int byteIndex = 0; byteIndex < destination.Length; byteIndex++)
            destination[byteIndex] = ExpectedChunkByte(chunkIndex, byteIndex);
    }

    private static byte ExpectedChunkByte(int chunkIndex, int byteIndex)
        => unchecked((byte)(
            31 +
            chunkIndex * 29 +
            byteIndex * 11));

    private static int SumRegionSizes()
    {
        int result = 0;
        for (int regionSize = 1; regionSize <= RegionCount; regionSize++)
            result += regionSize;
        return result;
    }

    private static uint GetPrivateUInt32Constant(string name)
    {
        FieldInfo field = RequiredField(typeof(VkCommandList), name);
        return Assert.IsType<uint>(field.GetRawConstantValue());
    }

    private static FieldInfo RequiredField(Type type, string name)
        => type.GetField(
               name,
               BindingFlags.Static |
               BindingFlags.Instance |
               BindingFlags.NonPublic) ??
           throw new InvalidOperationException(
               $"Required test seam '{type.FullName}.{name}' was not found.");

    private static uint AlignUp(int value, uint alignment)
    {
        uint unsignedValue = checked((uint)value);
        return AlignUp(unsignedValue, alignment);
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        uint remainder = value % alignment;
        return remainder == 0u
            ? value
            : checked(value + alignment - remainder);
    }
}
#endif
