#if TEST_VULKAN
using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public class VulkanRecordingPerformanceTests : GraphicsDeviceTestBase<VulkanDeviceCreator>
{
    private readonly ITestOutputHelper _output;

    public VulkanRecordingPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RepeatedIdenticalScissorRecordingDoesNotAllocate()
    {
        const int warmupCount = 256;
        const int measurementCount = 4_096;
        Texture target = RF.CreateTexture(TextureDescription.Texture2D(
            128,
            64,
            1,
            1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.RenderTarget));
        Framebuffer framebuffer = RF.CreateFramebuffer(new FramebufferDescription(null, target));
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.SetFramebuffer(framebuffer);
        commandList.SetScissorRect(0, 4, 8, 64, 32);
        for (int i = 0; i < warmupCount; i++)
        {
            commandList.SetScissorRect(0, 4, 8, 64, 32);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measurementCount; i++)
        {
            commandList.SetScissorRect(0, 4, 8, 64, 32);
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.Equal(0, allocatedBytes);
    }

    [Fact]
    public void BoundedSubmissionStateDoesNotAllocateAfterWarmup()
    {
        const int warmupCount = 32;
        const int measurementCount = 256;
        DeviceBuffer buffer = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.VertexBuffer));
        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 1,
            InitialTrackedResourceCapacityPerSubmission = 4
        });

        for (uint value = 1; value <= warmupCount; value++)
        {
            commandList.Begin();
            commandList.UpdateBuffer(buffer, 0, value);
            commandList.End();
            GD.SubmitCommands(commandList);
        }
        GD.WaitForIdle();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (uint value = 1; value <= measurementCount; value++)
        {
            commandList.Begin();
            commandList.UpdateBuffer(buffer, 0, value);
            commandList.End();
            GD.SubmitCommands(commandList);
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        GD.WaitForIdle();

        _output.WriteLine(
            $"Bounded submissions: {measurementCount}; allocated bytes: {allocatedBytes}; " +
            $"elapsed: {elapsed.TotalMilliseconds:F3} ms; " +
            $"average: {elapsed.TotalMicroseconds / measurementCount:F3} us/submission.");
        Assert.Equal(0, allocatedBytes);
    }
}
#endif
