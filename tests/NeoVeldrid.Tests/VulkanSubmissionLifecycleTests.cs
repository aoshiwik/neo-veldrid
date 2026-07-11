#if TEST_VULKAN
using System;
using System.Threading.Tasks;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "Vulkan")]
public class VulkanSubmissionLifecycleTests : GraphicsDeviceTestBase<VulkanDeviceCreator>
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void BoundedCommandListReusesSubmissionState(uint maximumInFlightCount)
    {
        DeviceBuffer buffer = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.VertexBuffer));
        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = maximumInFlightCount,
            InitialTrackedResourceCapacityPerSubmission = 4
        });

        const uint submissionCount = 32;
        for (uint value = 1; value <= submissionCount; value++)
        {
            commandList.Begin();
            commandList.UpdateBuffer(buffer, 0, value);
            commandList.End();
            GD.SubmitCommands(commandList);
        }

        GD.WaitForIdle();

        DeviceBuffer readback = GetReadback(buffer);
        MappedResourceView<uint> mapped = GD.Map<uint>(readback, MapMode.Read);
        Assert.Equal(submissionCount, mapped[0]);
        GD.Unmap(readback);
    }

    [Fact]
    public void ConcurrentDeviceUpdatesRecycleSharedSubmissionResources()
    {
        const int workerCount = 4;
        const uint updatesPerWorker = 64;
        DeviceBuffer[] buffers = new DeviceBuffer[workerCount];
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = RF.CreateBuffer(new BufferDescription(
                sizeof(uint),
                BufferUsage.VertexBuffer));
        }

        Parallel.For(0, workerCount, workerIndex =>
        {
            for (uint update = 1; update <= updatesPerWorker; update++)
            {
                uint value = ExpectedValue(workerIndex, update);
                GD.UpdateBuffer(buffers[workerIndex], 0, value);
            }
        });

        GD.WaitForIdle();

        for (int workerIndex = 0; workerIndex < buffers.Length; workerIndex++)
        {
            DeviceBuffer readback = GetReadback(buffers[workerIndex]);
            MappedResourceView<uint> mapped = GD.Map<uint>(readback, MapMode.Read);
            Assert.Equal(ExpectedValue(workerIndex, updatesPerWorker), mapped[0]);
            GD.Unmap(readback);
        }
    }

    private static uint ExpectedValue(int workerIndex, uint update)
        => ((uint)workerIndex + 1u) * 100_000u + update;
}
#endif
