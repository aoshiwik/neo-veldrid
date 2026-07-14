#if TEST_VULKAN
using System;
using System.Collections.Generic;
using System.Reflection;
using NeoVeldrid.Vk;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanStagedUploadIsolationTests
    : GraphicsDeviceTestBase<VulkanDeviceCreator>
{
    private const int BoundedSubmissionCapacity = 8;
    private const int SubmissionCount = BoundedSubmissionCapacity * 3;
    private const int RegionCount = 16;

    [Fact]
    public void BoundedSubmissionsPreserveEveryStagedUpdatePayload()
    {
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

        for (int submissionIndex = 0;
             submissionIndex < captures.Length;
             submissionIndex++)
        {
            commandList.Begin();
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
                payloadSize,
                uploadAlignment);
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
        Assert.Equal(
            CommandListBufferAccessKind.CopySourceAndDestination,
            finalCopy.Kind);
        Assert.Equal(sharedIdentity, finalCopy.PrimaryResourceIdentity);
        Assert.True(finalCopy.HasSecondaryResource);
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
