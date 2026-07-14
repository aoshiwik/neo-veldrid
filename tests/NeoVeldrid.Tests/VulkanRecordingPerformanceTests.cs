#if TEST_VULKAN
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    public void EightSlotManyResourceSubmissionStateDoesNotAllocateAfterWarmup()
    {
        const int warmupCount = 32;
        const int measurementCount = 128;
        const int resourceSetCount = 64;
        const uint maximumInFlightSubmissionCount = 8;

        uint uniformPayloadSize = checked((uint)Unsafe.SizeOf<DynamicUniformPayload>());
        uint uniformAlignment = Math.Max(1u, GD.UniformBufferMinOffsetAlignment);
        uint uniformStride = AlignUp(uniformPayloadSize, uniformAlignment);
        DeviceBuffer uniformBuffer = RF.CreateBuffer(new BufferDescription(
            checked(uniformStride * resourceSetCount),
            BufferUsage.UniformBuffer | BufferUsage.Dynamic));

        ResourceLayout layout = RF.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription(
                    "FrameValue",
                    ResourceKind.UniformBuffer,
                    ShaderStages.Compute,
                    ResourceLayoutElementOptions.DynamicBinding),
                new ResourceLayoutElementDescription(
                    "Output",
                    ResourceKind.StructuredBufferReadWrite,
                    ShaderStages.Compute)));
        Pipeline pipeline = RF.CreateComputePipeline(
            new ComputePipelineDescription(
                TestShaders.LoadCompute(RF, "DynamicUniformSlot"),
                layout,
                1,
                1,
                1));

        DeviceBuffer[] outputs = new DeviceBuffer[resourceSetCount];
        ResourceSet[] resourceSets = new ResourceSet[resourceSetCount];
        for (int resourceIndex = 0;
             resourceIndex < resourceSetCount;
             resourceIndex++)
        {
            uint dynamicOffset = checked((uint)resourceIndex * uniformStride);
            GD.UpdateBuffer(
                uniformBuffer,
                dynamicOffset,
                new DynamicUniformPayload(ExpectedResourceValue(resourceIndex)));
            outputs[resourceIndex] = RF.CreateBuffer(new BufferDescription(
                sizeof(uint),
                BufferUsage.StructuredBufferReadWrite,
                sizeof(uint)));
            resourceSets[resourceIndex] = RF.CreateResourceSet(
                new ResourceSetDescription(
                    layout,
                    new DeviceBufferRange(
                        uniformBuffer,
                        0,
                        uniformPayloadSize),
                    outputs[resourceIndex]));
        }
        GD.WaitForIdle();

        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = maximumInFlightSubmissionCount,
            InitialTrackedResourceCapacityPerSubmission = 256
        });
        Assert.Equal(
            maximumInFlightSubmissionCount,
            commandList.RecordingSubmissionSlotCount);

        for (int frameIndex = 0; frameIndex < warmupCount; frameIndex++)
        {
            SubmitRepresentativeFrame(
                commandList,
                pipeline,
                resourceSets,
                uniformStride);
        }
        GD.WaitForIdle();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int frameIndex = 0; frameIndex < measurementCount; frameIndex++)
        {
            SubmitRepresentativeFrame(
                commandList,
                pipeline,
                resourceSets,
                uniformStride);
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        GD.WaitForIdle();

        _output.WriteLine(
            $"Eight-slot submissions: {measurementCount}; " +
            $"resource sets per submission: {resourceSetCount}; " +
            $"allocated bytes: {allocatedBytes}; " +
            $"elapsed: {elapsed.TotalMilliseconds:F3} ms; " +
            $"average: {elapsed.TotalMicroseconds / measurementCount:F3} us/submission.");
        Assert.Equal(0, allocatedBytes);

        DeviceBuffer readback = GetReadback(outputs[resourceSetCount - 1]);
        MappedResourceView<uint> mapped = GD.Map<uint>(readback, MapMode.Read);
        try
        {
            Assert.Equal(
                ExpectedResourceValue(resourceSetCount - 1),
                mapped[0]);
        }
        finally
        {
            GD.Unmap(readback);
        }
    }

    private void SubmitRepresentativeFrame(
        CommandList commandList,
        Pipeline pipeline,
        ResourceSet[] resourceSets,
        uint uniformStride)
    {
        commandList.Begin();
        commandList.SetPipeline(pipeline);
        for (int resourceIndex = 0;
             resourceIndex < resourceSets.Length;
             resourceIndex++)
        {
            uint dynamicOffset = checked((uint)resourceIndex * uniformStride);
            commandList.SetComputeResourceSet(
                0,
                resourceSets[resourceIndex],
                1,
                ref dynamicOffset);
            commandList.Dispatch(1, 1, 1);
        }
        commandList.End();
        GD.SubmitCommands(commandList);
    }

    private static uint ExpectedResourceValue(int resourceIndex)
        => checked(50_000u + (uint)resourceIndex * 131u);

    private static uint AlignUp(uint value, uint alignment)
    {
        uint remainder = value % alignment;
        return remainder == 0u
            ? value
            : checked(value + alignment - remainder);
    }

    private readonly struct DynamicUniformPayload
    {
        public readonly uint Value;
        public readonly uint Padding0;
        public readonly uint Padding1;
        public readonly uint Padding2;

        public DynamicUniformPayload(uint value)
        {
            Value = value;
            Padding0 = 0;
            Padding1 = 0;
            Padding2 = 0;
        }
    }
}
#endif
