#if TEST_VULKAN
using System;
using System.Threading;
using System.Threading.Tasks;
using NeoVeldrid.Vk;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanSubmissionFailureOwnershipTests
    : GraphicsDeviceTestBase<VulkanDeviceCreator>
{
    [Theory]
    [InlineData((int)VkGraphicsDevice.SubmissionCheckpoint.BeforeCompletionFenceAcquisition)]
    [InlineData((int)VkGraphicsDevice.SubmissionCheckpoint.BeforePrimaryQueueSubmit)]
    public void PrePrimaryBufferUploadFailureRecyclesEveryTransientResourceExactlyOnce(
        int checkpointValue)
    {
        var checkpoint = (VkGraphicsDevice.SubmissionCheckpoint)checkpointValue;
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            64,
            BufferUsage.VertexBuffer));
        byte[] payload = CreatePayload(64, 17);

        // Warm each pool so an exact before/after count proves that the failed
        // transaction returned, but did not duplicate, every borrowed object.
        GD.UpdateBuffer(target, 0, payload);
        GD.WaitForIdle();
        VkGraphicsDevice.SubmissionResourcePoolSnapshot before =
            graphicsDevice.CaptureSubmissionResourcePoolSnapshot();

        var failure = new ThrowOnceAtCheckpoint(checkpoint);
        graphicsDevice.SubmissionCheckpointObserver = failure;
        try
        {
            Assert.Throws<InjectedSubmissionFailureException>(
                () => GD.UpdateBuffer(target, 0, payload));
        }
        finally
        {
            graphicsDevice.SubmissionCheckpointObserver = null;
        }

        Assert.True(failure.WasTriggered);
        AssertPoolSnapshotEqual(
            before,
            graphicsDevice.CaptureSubmissionResourcePoolSnapshot());

        byte[] finalPayload = CreatePayload(64, 91);
        GD.UpdateBuffer(target, 0, finalPayload);
        GD.WaitForIdle();
        AssertBufferContents(target, finalPayload);
    }

    [Fact]
    public void PrePrimaryTextureUploadFailureRecyclesTextureAndCommandPoolExactlyOnce()
    {
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        const uint width = 4;
        const uint height = 4;
        Texture target = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.Sampled));
        byte[] payload = CreatePayload(checked((int)(width * height * 4)), 23);

        Texture warmTarget = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.Sampled));
        GD.UpdateTexture(
            warmTarget,
            payload,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        GD.WaitForIdle();
        VkGraphicsDevice.SubmissionResourcePoolSnapshot before =
            graphicsDevice.CaptureSubmissionResourcePoolSnapshot();

        var failure = new ThrowOnceAtCheckpoint(
            VkGraphicsDevice.SubmissionCheckpoint.BeforePrimaryQueueSubmit);
        graphicsDevice.SubmissionCheckpointObserver = failure;
        try
        {
            Assert.Throws<InjectedSubmissionFailureException>(() =>
                GD.UpdateTexture(
                    target,
                    payload,
                    0,
                    0,
                    0,
                    width,
                    height,
                    1,
                    0,
                    0));
        }
        finally
        {
            graphicsDevice.SubmissionCheckpointObserver = null;
        }

        Assert.True(failure.WasTriggered);
        AssertPoolSnapshotEqual(
            before,
            graphicsDevice.CaptureSubmissionResourcePoolSnapshot());
        Assert.Equal(
            Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal,
            Assert.IsType<VkTexture>(target).GetImageLayout(0, 0));

        byte[] recoveredPayload = CreatePayload(
            checked((int)(width * height * 4)),
            71);
        GD.UpdateTexture(
            target,
            recoveredPayload,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);

        Texture readback = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.Staging));
        CommandList commandList = RF.CreateCommandList();
        commandList.Begin();
        commandList.CopyTexture(target, readback);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        MappedResourceView<byte> mapped = GD.Map<byte>(readback, MapMode.Read);
        try
        {
            for (int i = 0; i < recoveredPayload.Length; i++)
                Assert.Equal(recoveredPayload[i], mapped[checked((uint)i)]);
        }
        finally
        {
            GD.Unmap(readback);
        }
    }

    [Theory]
    [InlineData((int)VkGraphicsDevice.SubmissionCheckpoint.BeforeAuxiliaryCompletionSubmit)]
    [InlineData((int)VkGraphicsDevice.SubmissionCheckpoint.BeforeCompletionTracking)]
    public void PostPrimaryFailureCompletesWithoutRetainingCallerFence(
        int checkpointValue)
    {
        var checkpoint = (VkGraphicsDevice.SubmissionCheckpoint)checkpointValue;
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);

        // Prime the internal completion-fence pool so the exceptional handoff
        // must return the same borrowed fence rather than merely create one.
        DeviceBuffer warmTarget = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.VertexBuffer));
        GD.UpdateBuffer(warmTarget, 0, 1u);
        GD.WaitForIdle();

        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.Staging));
        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 1,
            InitialTrackedResourceCapacityPerSubmission = 4
        });
        commandList.Begin();
        commandList.UpdateBuffer(target, 0, 101u);
        commandList.End();

        VkCommandList vkCommandList = Assert.IsType<VkCommandList>(commandList);
        nint submittedCommandBuffer = vkCommandList.CommandBuffer.Handle;
        Fence callerFence = RF.CreateFence(false);
        VkGraphicsDevice.SubmissionResourcePoolSnapshot before =
            graphicsDevice.CaptureSubmissionResourcePoolSnapshot();

        var failure = new ThrowOnceAtCheckpoint(checkpoint);
        graphicsDevice.SubmissionCheckpointObserver = failure;
        try
        {
            Assert.Throws<InjectedSubmissionFailureException>(
                () => GD.SubmitCommands(commandList, callerFence));
        }
        finally
        {
            graphicsDevice.SubmissionCheckpointObserver = null;
        }

        Assert.True(failure.WasTriggered);
        VkGraphicsDevice.SubmissionResourcePoolSnapshot after =
            graphicsDevice.CaptureSubmissionResourcePoolSnapshot();
        AssertPoolSnapshotEqual(before, after);

        // The failed API call has already synchronously proven primary queue
        // completion, so the caller can immediately reset and release its fence.
        GD.ResetFence(callerFence);
        callerFence.Dispose();

        commandList.Begin();
        Assert.Equal(submittedCommandBuffer, vkCommandList.CommandBuffer.Handle);
        commandList.UpdateBuffer(target, 0, 202u);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        MappedResourceView<uint> mapped = GD.Map<uint>(target, MapMode.Read);
        try
        {
            Assert.Equal(202u, mapped[0]);
        }
        finally
        {
            GD.Unmap(target);
        }
    }

    [Fact]
    public async Task CallerFenceSignalsAfterInternalTrackingFenceCanBeReclaimed()
    {
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 1,
            InitialTrackedResourceCapacityPerSubmission = 4
        });
        commandList.Begin();
        commandList.UpdateBuffer(target, 0, 101u);
        commandList.End();

        Fence callerFence = RF.CreateFence(false);
        using var checkpoint = new BlockAtCheckpoint(
            VkGraphicsDevice.SubmissionCheckpoint.BeforeAuxiliaryCompletionSubmit);
        graphicsDevice.SubmissionCheckpointObserver = checkpoint;
        Task submissionTask = Task.Run(() => GD.SubmitCommands(commandList, callerFence));
        bool checkpointReached;
        bool callerFenceSignaledBeforeAuxiliarySubmit;
        try
        {
            checkpointReached = checkpoint.WaitUntilEntered(TimeSpan.FromSeconds(5));
            callerFenceSignaledBeforeAuxiliarySubmit = checkpointReached
                && GD.WaitForFence(callerFence, TimeSpan.FromMilliseconds(100));

            checkpoint.Release();
            await submissionTask;
        }
        finally
        {
            checkpoint.Release();
            graphicsDevice.SubmissionCheckpointObserver = null;
        }

        Assert.True(checkpointReached);
        Assert.False(callerFenceSignaledBeforeAuxiliarySubmit);

        GD.WaitForFence(callerFence);
        GD.UpdateBuffer(target, 0, 202u);
        GD.Map(target, MapMode.Write);
        GD.Unmap(target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeginRecyclesAbandonedEndedCommandBufferWithoutReleasingRecordedResources(
        bool rejectFirstSubmission)
    {
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.Staging));
        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 1,
            InitialTrackedResourceCapacityPerSubmission = 4
        });
        VkCommandList vkCommandList = Assert.IsType<VkCommandList>(commandList);

        commandList.Begin();
        commandList.UpdateBuffer(target, 0, 303u);
        commandList.End();
        nint abandonedCommandBuffer = vkCommandList.CommandBuffer.Handle;

        if (rejectFirstSubmission)
        {
            var failure = new ThrowOnceAtCheckpoint(
                VkGraphicsDevice.SubmissionCheckpoint.BeforePrimaryQueueSubmit);
            graphicsDevice.SubmissionCheckpointObserver = failure;
            try
            {
                Assert.Throws<InjectedSubmissionFailureException>(
                    () => GD.SubmitCommands(commandList));
            }
            finally
            {
                graphicsDevice.SubmissionCheckpointObserver = null;
            }

            Assert.True(failure.WasTriggered);
        }

        commandList.Begin();
        Assert.Equal(abandonedCommandBuffer, vkCommandList.CommandBuffer.Handle);
        commandList.UpdateBuffer(target, 0, 404u);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        MappedResourceView<uint> mapped = GD.Map<uint>(target, MapMode.Read);
        try
        {
            Assert.Equal(404u, mapped[0]);
        }
        finally
        {
            GD.Unmap(target);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedTextureRecordingCanBeRetriedOrTransactionallyAbandoned(
        bool abandonRejectedRecording)
    {
        const uint width = 4;
        const uint height = 4;
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        Texture target = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Storage));
        VkTexture vkTarget = Assert.IsType<VkTexture>(target);
        byte[] rejectedPayload = CreatePayload(checked((int)(width * height)), 19);
        byte[] replacementPayload = CreatePayload(rejectedPayload.Length, 83);
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.UpdateTexture(
            target,
            rejectedPayload,
            0,
            0,
            0,
            width,
            height,
            1,
            0,
            0);
        commandList.End();
        Assert.Equal(
            Silk.NET.Vulkan.ImageLayout.General,
            vkTarget.GetImageLayout(0, 0));

        var failure = new ThrowOnceAtCheckpoint(
            VkGraphicsDevice.SubmissionCheckpoint.BeforePrimaryQueueSubmit);
        graphicsDevice.SubmissionCheckpointObserver = failure;
        try
        {
            Assert.Throws<InjectedSubmissionFailureException>(
                () => GD.SubmitCommands(commandList));
        }
        finally
        {
            graphicsDevice.SubmissionCheckpointObserver = null;
        }

        Assert.True(failure.WasTriggered);
        Assert.Equal(
            Silk.NET.Vulkan.ImageLayout.General,
            vkTarget.GetImageLayout(0, 0));

        byte[] expected;
        if (abandonRejectedRecording)
        {
            commandList.Begin();
            Assert.Equal(
                Silk.NET.Vulkan.ImageLayout.Undefined,
                vkTarget.GetImageLayout(0, 0));
            commandList.UpdateTexture(
                target,
                replacementPayload,
                0,
                0,
                0,
                width,
                height,
                1,
                0,
                0);
            commandList.End();
            expected = replacementPayload;
        }
        else
        {
            expected = rejectedPayload;
        }

        GD.SubmitCommands(commandList);

        Texture readback = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Staging));
        CommandList copy = RF.CreateCommandList();
        copy.Begin();
        copy.CopyTexture(target, readback);
        copy.End();
        GD.SubmitCommands(copy);
        GD.WaitForIdle();

        MappedResourceView<byte> mapped = GD.Map<byte>(readback, MapMode.Read);
        try
        {
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], mapped[checked((uint)i)]);
        }
        finally
        {
            GD.Unmap(readback);
        }
    }

    private void AssertBufferContents(DeviceBuffer target, byte[] expected)
    {
        DeviceBuffer readback = GetReadback(target);
        MappedResourceView<byte> mapped = GD.Map<byte>(readback, MapMode.Read);
        try
        {
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], mapped[checked((uint)i)]);
        }
        finally
        {
            GD.Unmap(readback);
        }
    }

    private static void AssertPoolSnapshotEqual(
        VkGraphicsDevice.SubmissionResourcePoolSnapshot expected,
        VkGraphicsDevice.SubmissionResourcePoolSnapshot actual)
    {
        Assert.Equal(
            expected.AvailableSharedCommandPoolCount,
            actual.AvailableSharedCommandPoolCount);
        Assert.Equal(
            expected.AvailableStagingTextureCount,
            actual.AvailableStagingTextureCount);
        Assert.Equal(
            expected.AvailableStagingBufferCount,
            actual.AvailableStagingBufferCount);
        Assert.Equal(
            expected.AvailableSubmissionFenceCount,
            actual.AvailableSubmissionFenceCount);
        Assert.Equal(expected.TrackedSubmissionCount, actual.TrackedSubmissionCount);
        Assert.Equal(
            expected.UnresolvedSubmissionCount,
            actual.UnresolvedSubmissionCount);
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        byte[] payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = unchecked((byte)(seed + i * 31));
        return payload;
    }

    private sealed class ThrowOnceAtCheckpoint
        : VkGraphicsDevice.ISubmissionCheckpointObserver
    {
        private readonly VkGraphicsDevice.SubmissionCheckpoint _checkpoint;

        public bool WasTriggered { get; private set; }

        public ThrowOnceAtCheckpoint(
            VkGraphicsDevice.SubmissionCheckpoint checkpoint)
        {
            _checkpoint = checkpoint;
        }

        public void OnCheckpoint(VkGraphicsDevice.SubmissionCheckpoint checkpoint)
        {
            if (WasTriggered || checkpoint != _checkpoint)
                return;

            WasTriggered = true;
            throw new InjectedSubmissionFailureException(checkpoint);
        }
    }

    private sealed class BlockAtCheckpoint
        : VkGraphicsDevice.ISubmissionCheckpointObserver, IDisposable
    {
        private readonly VkGraphicsDevice.SubmissionCheckpoint _checkpoint;
        private readonly ManualResetEventSlim _entered = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);

        public BlockAtCheckpoint(VkGraphicsDevice.SubmissionCheckpoint checkpoint)
        {
            _checkpoint = checkpoint;
        }

        public void OnCheckpoint(VkGraphicsDevice.SubmissionCheckpoint checkpoint)
        {
            if (checkpoint != _checkpoint)
                return;

            _entered.Set();
            _release.Wait();
        }

        public bool WaitUntilEntered(TimeSpan timeout)
            => _entered.Wait(timeout);

        public void Release()
            => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private sealed class InjectedSubmissionFailureException : Exception
    {
        public InjectedSubmissionFailureException(
            VkGraphicsDevice.SubmissionCheckpoint checkpoint)
            : base($"Injected Vulkan submission failure at {checkpoint}.")
        {
        }
    }
}
#endif
