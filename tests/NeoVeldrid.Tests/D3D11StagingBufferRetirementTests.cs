#if TEST_D3D11
using NeoVeldrid.D3D11;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "D3D11")]
public sealed class D3D11StagingBufferRetirementTests : GraphicsDeviceTestBase<D3D11DeviceCreator>
{
    [SkippableFact]
    public void SubmissionRetainsStagingUntilGpuCompletionAndBoundedReusePreservesEveryCopy()
    {
        const int frames = 40;
        using DeviceBuffer uniform = RF.CreateBuffer(new BufferDescription(1024,
            BufferUsage.UniformBuffer | BufferUsage.Dynamic));
        using DeviceBuffer readback = RF.CreateBuffer(new BufferDescription(frames * 16, BufferUsage.Staging));
        using var commands = (D3D11CommandList)RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 2
        });
        for (uint frame = 0; frame < frames; frame++)
        {
            commands.Begin();
            commands.UpdateBuffer(uniform, 256, new uint[] { frame, frame + 1, frame + 2, frame + 3 });
            commands.CopyBuffer(uniform, 256, readback, frame * 16, 16);
            commands.End();
            Assert.Equal(1, commands.StagingUploadDiagnostics.RecordingBuffers);
            GD.SubmitCommands(commands);
            var ownership = commands.StagingUploadDiagnostics;
            Assert.Equal(0, ownership.RecordingBuffers);
            Assert.InRange(ownership.PendingSubmissions, 1, 2);
            Assert.InRange(ownership.SubmissionSlots, 1, 2);
            if (frame == 0)
                Assert.Equal(0, ownership.AvailableBuffers);
        }
        GD.WaitForIdle();
        var mapped = GD.Map<uint>(readback, MapMode.Read);
        try
        {
            for (uint frame = 0; frame < frames; frame++)
                for (uint component = 0; component < 4; component++)
                    Assert.Equal(frame + component, mapped[frame * 4 + component]);
        }
        finally { GD.Unmap(readback); }
        Assert.InRange(commands.StagingUploadDiagnostics.CreatedBuffers, 1, 2);
        GD.CheckValidation("GPU-completed staging reuse across bounded D3D11 submissions");
    }

    [SkippableFact]
    public void ImmediatePartialUpdatesRetainStagingAndPreserveEveryCopy()
    {
        const int frames = 40;
        using DeviceBuffer uniform = RF.CreateBuffer(new BufferDescription(1024,
            BufferUsage.UniformBuffer | BufferUsage.Dynamic));
        using DeviceBuffer readback = RF.CreateBuffer(new BufferDescription(frames * 16, BufferUsage.Staging));
        using var commands = RF.CreateCommandList();
        var device = (D3D11GraphicsDevice)GD;
        for (uint frame = 0; frame < frames; frame++)
        {
            GD.UpdateBuffer(uniform, 256, new uint[] { frame, frame + 1, frame + 2, frame + 3 });
            var ownership = device.ImmediateStagingUploadDiagnostics;
            Assert.Equal(0, ownership.RecordingBuffers);
            Assert.InRange(ownership.PendingSubmissions, 1, 3);
            Assert.InRange(ownership.SubmissionSlots, 1, 3);
            if (frame == 0)
                Assert.Equal(0, ownership.AvailableBuffers);
            commands.Begin();
            commands.CopyBuffer(uniform, 256, readback, frame * 16, 16);
            commands.End();
            GD.SubmitCommands(commands);
        }
        GD.WaitForIdle();
        var mapped = GD.Map<uint>(readback, MapMode.Read);
        try
        {
            for (uint frame = 0; frame < frames; frame++)
                for (uint component = 0; component < 4; component++)
                    Assert.Equal(frame + component, mapped[frame * 4 + component]);
        }
        finally { GD.Unmap(readback); }
        Assert.InRange(device.ImmediateStagingUploadDiagnostics.CreatedBuffers, 1, 3);
        GD.CheckValidation("GPU-completed staging reuse for immediate D3D11 partial updates");
    }

    [SkippableFact]
    public void AbandoningUnsubmittedRecordingReusesItsStorageWithoutWaitingForGpu()
    {
        using DeviceBuffer uniform = RF.CreateBuffer(new BufferDescription(1024,
            BufferUsage.UniformBuffer | BufferUsage.Dynamic));
        using var commands = (D3D11CommandList)RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 1
        });
        commands.Begin();
        commands.UpdateBuffer(uniform, 256, new uint[] { 1, 2, 3, 4 });
        commands.End();
        Assert.Equal(1, commands.StagingUploadDiagnostics.RecordingBuffers);
        commands.Begin(); // Drops an ended recording that never reached SubmitCommands.
        Assert.Equal(0, commands.StagingUploadDiagnostics.PendingSubmissions);
        commands.UpdateBuffer(uniform, 256, new uint[] { 5, 6, 7, 8 });
        commands.End();
        Assert.Equal(1, commands.StagingUploadDiagnostics.CreatedBuffers);
        Assert.Equal(0, commands.StagingUploadDiagnostics.CapacityWaits);
        GD.SubmitCommands(commands);
        GD.WaitForIdle();
        commands.Dispose(); commands.Dispose();
        GD.CheckValidation("abandoned D3D11 staging ownership");
    }
}
#endif
