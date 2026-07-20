#if TEST_VULKAN
using System;
using NeoVeldrid.Vk;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public sealed class VulkanCommandListSubmissionDiagnosticsFacadeTests
    : GraphicsDeviceTestBase<VulkanDeviceCreator>
{
    [Fact]
    public void PublicFacadePublishesOnlySuccessfulDiagnosedSubmissions()
    {
        CommandList commandList = CreateCommandList();
        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            32,
            BufferUsage.Staging));

        Assert.False(commandList.SubmissionDiagnosticsEnabled);
        Assert.False(commandList.TryGetLastSubmissionMetrics(out var disabled));
        Assert.Equal(default, disabled);
        Assert.Null(commandList.CaptureLastSubmissionDiagnostics());

        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 2);

        Assert.True(commandList.SubmissionDiagnosticsEnabled);
        Assert.False(commandList.TryGetLastSubmissionMetrics(out var enabled));
        Assert.Equal(default, enabled);

        RecordUpdate(commandList, target, 0, new byte[4]);

        Assert.False(commandList.TryGetLastSubmissionMetrics(out var recorded));
        Assert.Equal(default, recorded);
        Assert.Null(commandList.CaptureLastSubmissionDiagnostics());

        GD.SubmitCommands(commandList);

        Assert.True(commandList.TryGetLastSubmissionMetrics(out var submitted));
        Assert.Equal(1L, submitted.SubmissionSequence);
        Assert.Equal(1, submitted.UpdateBufferCallCount);
        Assert.Equal(4UL, submitted.UpdatedBufferBytes);
        CommandListSubmissionSnapshot snapshot =
            commandList.CaptureLastSubmissionDiagnostics();
        Assert.NotNull(snapshot);
        Assert.Equal(submitted, snapshot.Metrics);
    }

    [Fact]
    public void FailedSubmissionPreservesPriorMetricsAndRetryPublishesExactlyOnce()
    {
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        CommandList commandList = CreateCommandList();
        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            32,
            BufferUsage.Staging));
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 2);

        RecordUpdate(commandList, target, 0, new byte[4]);
        GD.SubmitCommands(commandList);
        Assert.True(commandList.TryGetLastSubmissionMetrics(out var first));
        Assert.Equal(1L, first.SubmissionSequence);
        Assert.Equal(4UL, first.UpdatedBufferBytes);

        RecordUpdate(commandList, target, 8, new byte[8]);
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
        Assert.True(commandList.TryGetLastSubmissionMetrics(out var afterFailure));
        Assert.Equal(first, afterFailure);
        CommandListSubmissionSnapshot snapshotAfterFailure =
            commandList.CaptureLastSubmissionDiagnostics();
        Assert.NotNull(snapshotAfterFailure);
        Assert.Equal(first, snapshotAfterFailure.Metrics);

        GD.SubmitCommands(commandList);

        Assert.True(commandList.TryGetLastSubmissionMetrics(out var retried));
        Assert.Equal(2L, retried.SubmissionSequence);
        Assert.Equal(1, retried.UpdateBufferCallCount);
        Assert.Equal(8UL, retried.UpdatedBufferBytes);
        Assert.NotEqual(first.CommandSignature, retried.CommandSignature);
    }

    [Fact]
    public void ValidationFailureAfterNativeSubmissionStillCommitsDiagnostics()
    {
        CommandList commandList = CreateCommandList();
        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            32,
            BufferUsage.Staging));
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 2);

        RecordUpdate(commandList, target, 0, new byte[4]);
        GD.Validation.Report(
            GraphicsDeviceValidationSeverity.Error,
            "test",
            "submission-boundary",
            "committed-submission-sentinel",
            "Synthetic validation failure after a committed native submission.");

        GraphicsDeviceValidationException exception =
            Assert.Throws<GraphicsDeviceValidationException>(
                () => GD.SubmitCommands(commandList));
        Assert.Contains(
            exception.Messages,
            message => message.Id == "committed-submission-sentinel");

        Assert.True(commandList.TryGetLastSubmissionMetrics(out var committed));
        Assert.Equal(1L, committed.SubmissionSequence);
        Assert.Equal(4UL, committed.UpdatedBufferBytes);

        // The native operation succeeded even though its validation boundary
        // failed. Reuse must advance from the committed recording rather than
        // treating it as a retryable pre-submission failure.
        RecordUpdate(commandList, target, 8, new byte[8]);
        GD.SubmitCommands(commandList);
        Assert.True(commandList.TryGetLastSubmissionMetrics(out var reused));
        Assert.Equal(2L, reused.SubmissionSequence);
        Assert.Equal(8UL, reused.UpdatedBufferBytes);
    }

    [Fact]
    public void DisableAndReenableStartANewDiagnosticsLifetime()
    {
        CommandList commandList = CreateCommandList();
        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            16,
            BufferUsage.Staging));
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 1);

        RecordUpdate(commandList, target, 0, new byte[4]);
        GD.SubmitCommands(commandList);
        CommandListSubmissionSnapshot frozen =
            commandList.CaptureLastSubmissionDiagnostics();
        Assert.NotNull(frozen);

        commandList.DisableSubmissionDiagnostics();

        Assert.False(commandList.SubmissionDiagnosticsEnabled);
        Assert.False(commandList.TryGetLastSubmissionMetrics(out var disabled));
        Assert.Equal(default, disabled);
        Assert.Null(commandList.CaptureLastSubmissionDiagnostics());
        Assert.Equal(1L, frozen.Metrics.SubmissionSequence);

        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 1);

        Assert.True(commandList.SubmissionDiagnosticsEnabled);
        Assert.False(commandList.TryGetLastSubmissionMetrics(out var reenabled));
        Assert.Equal(default, reenabled);

        RecordUpdate(commandList, target, 4, new byte[8]);
        GD.SubmitCommands(commandList);

        Assert.True(commandList.TryGetLastSubmissionMetrics(out var secondLifetime));
        Assert.Equal(1L, secondLifetime.SubmissionSequence);
        Assert.Equal(8UL, secondLifetime.UpdatedBufferBytes);
        Assert.Equal(1L, frozen.Metrics.SubmissionSequence);
        Assert.Equal(4UL, frozen.Metrics.UpdatedBufferBytes);
    }

    [Fact]
    public void DiagnosticsConfigurationIsRejectedDuringActiveRecording()
    {
        CommandList commandList = CreateCommandList();

        commandList.Begin();
        Assert.Throws<InvalidOperationException>(
            () => commandList.EnableSubmissionDiagnostics());
        commandList.End();

        commandList.EnableSubmissionDiagnostics();
        commandList.Begin();
        Assert.Throws<InvalidOperationException>(
            () => commandList.EnableSubmissionDiagnostics());
        Assert.Throws<InvalidOperationException>(
            () => commandList.DisableSubmissionDiagnostics());
        commandList.End();

        commandList.DisableSubmissionDiagnostics();
        Assert.False(commandList.SubmissionDiagnosticsEnabled);
    }

    [Fact]
    public void MetricsFacadeDoesNotAllocateAfterSuccessfulSubmission()
    {
        CommandList commandList = CreateCommandList();
        DeviceBuffer target = RF.CreateBuffer(new BufferDescription(
            16,
            BufferUsage.Staging));
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 1);
        RecordUpdate(commandList, target, 0, new byte[4]);
        GD.SubmitCommands(commandList);

        Assert.True(commandList.TryGetLastSubmissionMetrics(out _));
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 4096; i++)
        {
            if (!commandList.TryGetLastSubmissionMetrics(out _))
                throw new InvalidOperationException("Expected submitted metrics.");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
    }

    private CommandList CreateCommandList()
    {
        return RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 1,
            InitialTrackedResourceCapacityPerSubmission = 4,
        });
    }

    private static void RecordUpdate(
        CommandList commandList,
        DeviceBuffer target,
        uint offset,
        byte[] payload)
    {
        commandList.Begin();
        commandList.UpdateBuffer(target, offset, payload);
        commandList.End();
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
