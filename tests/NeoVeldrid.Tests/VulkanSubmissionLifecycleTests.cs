#if TEST_VULKAN
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NeoVeldrid.Vk;
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

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(8u)]
    public void RecordingSubmissionSlotsPreserveDynamicUniformPayloads(
        uint maximumInFlightCount)
    {
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        const int uniformSliceCount = 48;
        uint uniformPayloadSize = checked((uint)Unsafe.SizeOf<DynamicUniformPayload>());
        uint uniformAlignment = Math.Max(1u, GD.UniformBufferMinOffsetAlignment);
        uint uniformStride = AlignUp(uniformPayloadSize, uniformAlignment);
        uint uniformBufferSize = checked(uniformStride * uniformSliceCount);

        DeviceBuffer[] frameBuffers = new DeviceBuffer[maximumInFlightCount];
        ResourceSet[] frameSets = new ResourceSet[maximumInFlightCount];
        DeviceBuffer shaderOutput = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.StructuredBufferReadWrite,
            sizeof(uint)));

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

        for (int slot = 0; slot < frameBuffers.Length; slot++)
        {
            frameBuffers[slot] = RF.CreateBuffer(new BufferDescription(
                uniformBufferSize,
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            frameSets[slot] = RF.CreateResourceSet(
                new ResourceSetDescription(
                    layout,
                    new DeviceBufferRange(
                        frameBuffers[slot],
                        0,
                        uniformPayloadSize),
                    shaderOutput));
        }

        int submissionCount = checked((int)maximumInFlightCount * 3);
        DeviceBuffer[] captures = new DeviceBuffer[submissionCount];
        for (int submissionIndex = 0;
             submissionIndex < captures.Length;
             submissionIndex++)
        {
            captures[submissionIndex] = RF.CreateBuffer(new BufferDescription(
                checked((uint)(uniformSliceCount * sizeof(uint))),
                BufferUsage.Staging));
        }

        CommandList commandList = RF.CreateCommandList(
            new CommandListDescription
            {
                MaximumInFlightSubmissionCount = maximumInFlightCount,
                InitialTrackedResourceCapacityPerSubmission = 8
            });
        Assert.Equal(
            maximumInFlightCount,
            commandList.RecordingSubmissionSlotCount);
        AssertRecordingSlotUnavailable(commandList);
        GD.WaitForIdle();
        using var reclamationGate =
            new VulkanAutomaticSubmissionReclamationGate(graphicsDevice);
        var retainedSlots = new HashSet<uint>();
        uint oldestRetainedSlot = uint.MaxValue;
        uint? alreadyBegunSubmissionSlot = null;

        for (int submissionIndex = 0;
             submissionIndex < captures.Length;
             submissionIndex++)
        {
            uint slot;
            if (alreadyBegunSubmissionSlot.HasValue)
            {
                slot = alreadyBegunSubmissionSlot.GetValueOrDefault();
                alreadyBegunSubmissionSlot = null;
            }
            else
            {
                commandList.Begin();
                slot = commandList.RecordingSubmissionSlot;
            }

            Assert.InRange(slot, 0u, maximumInFlightCount - 1u);
            if (submissionIndex < maximumInFlightCount)
            {
                Assert.True(
                    retainedSlots.Add(slot),
                    $"Submission slot {slot} was reused before automatic reclamation was released.");
                if (submissionIndex == 0)
                    oldestRetainedSlot = slot;
            }

            // Begin reserves this frame version until the resulting submission
            // completes. Exercise the same aligned, per-draw dynamic slices
            // used by Domain instead of merely binding one whole uniform buffer.
            commandList.SetPipeline(pipeline);
            for (int sliceIndex = 0;
                 sliceIndex < uniformSliceCount;
                 sliceIndex++)
            {
                uint dynamicOffset = checked((uint)sliceIndex * uniformStride);
                var payload = new DynamicUniformPayload(
                    ExpectedSlotValue(submissionIndex, sliceIndex));
                GD.UpdateBuffer(
                    frameBuffers[slot],
                    dynamicOffset,
                    payload);
                commandList.SetComputeResourceSet(
                    0,
                    frameSets[slot],
                    1,
                    ref dynamicOffset);
                commandList.Dispatch(1, 1, 1);
                commandList.CopyBuffer(
                    shaderOutput,
                    0,
                    captures[submissionIndex],
                    checked((uint)(sliceIndex * sizeof(uint))),
                    sizeof(uint));
            }
            commandList.End();
            AssertRecordingSlotUnavailable(commandList);

            GD.SubmitCommands(commandList);
            if (submissionIndex == maximumInFlightCount - 1)
            {
                Assert.Equal(
                    checked((int)maximumInFlightCount),
                    retainedSlots.Count);
                Assert.Equal(
                    checked((int)maximumInFlightCount),
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
                    "The capacity-plus-one Begin reused retained submission state before completion.");
                Assert.Equal(
                    checked((int)maximumInFlightCount),
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
            MappedResourceView<uint> mapped = GD.Map<uint>(
                captures[submissionIndex],
                MapMode.Read);
            try
            {
                for (int sliceIndex = 0;
                     sliceIndex < uniformSliceCount;
                     sliceIndex++)
                {
                    Assert.Equal(
                        ExpectedSlotValue(submissionIndex, sliceIndex),
                        mapped[checked((uint)sliceIndex)]);
                }
            }
            finally
            {
                GD.Unmap(captures[submissionIndex]);
            }
        }
    }

    [Fact]
    public void WaitForIdleReleasesOwnershipWhileAutomaticReclamationIsPaused()
    {
        VkGraphicsDevice graphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        DeviceBuffer buffer = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.VertexBuffer));
        CommandList commandList = RF.CreateCommandList(
            new CommandListDescription
            {
                MaximumInFlightSubmissionCount = 1,
                InitialTrackedResourceCapacityPerSubmission = 2
            });

        GD.WaitForIdle();
        using var reclamationGate =
            new VulkanAutomaticSubmissionReclamationGate(graphicsDevice);
        commandList.Begin();
        commandList.UpdateBuffer(buffer, 0, 42u);
        commandList.End();
        GD.SubmitCommands(commandList);
        Assert.Equal(
            1,
            graphicsDevice
                .CaptureSubmissionResourcePoolSnapshot()
                .TrackedSubmissionCount);

        GD.WaitForIdle();

        Assert.Equal(
            0,
            graphicsDevice
                .CaptureSubmissionResourcePoolSnapshot()
                .TrackedSubmissionCount);
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

    [Fact]
    public async Task IndependentBoundedCommandListsRecycleConcurrently()
    {
        const int workerCount = 2;
        const uint submissionCount = 48;
        DeviceBuffer[] buffers = new DeviceBuffer[workerCount];
        CommandList[] commandLists = new CommandList[workerCount];
        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            buffers[workerIndex] = RF.CreateBuffer(new BufferDescription(
                sizeof(uint),
                BufferUsage.VertexBuffer));
            commandLists[workerIndex] = RF.CreateCommandList(new CommandListDescription
            {
                MaximumInFlightSubmissionCount = 1,
                InitialTrackedResourceCapacityPerSubmission = 2
            });
        }

        using Barrier submissionBarrier = new Barrier(workerCount);
        Task[] workers = new Task[workerCount];
        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            int capturedWorkerIndex = workerIndex;
            workers[workerIndex] = Task.Run(() =>
            {
                CommandList commandList = commandLists[capturedWorkerIndex];
                DeviceBuffer buffer = buffers[capturedWorkerIndex];
                for (uint submission = 1; submission <= submissionCount; submission++)
                {
                    if (!submissionBarrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                        throw new TimeoutException("Concurrent Vulkan submission workers did not rendezvous.");

                    commandList.Begin();
                    commandList.UpdateBuffer(
                        buffer,
                        0,
                        ExpectedValue(capturedWorkerIndex, submission));
                    commandList.End();
                    GD.SubmitCommands(commandList);
                }
            });
        }

        await Task.WhenAll(workers);
        GD.WaitForIdle();

        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            DeviceBuffer readback = GetReadback(buffers[workerIndex]);
            MappedResourceView<uint> mapped = GD.Map<uint>(readback, MapMode.Read);
            Assert.Equal(ExpectedValue(workerIndex, submissionCount), mapped[0]);
            GD.Unmap(readback);
        }
    }

    [Fact]
    public async Task BoundedSubmissionWaitDoesNotBlockUnrelatedSubmission()
    {
        const uint firstValue = 101;
        const uint secondValue = 202;
        DeviceBuffer[] buffers = new DeviceBuffer[2];
        CommandList[] commandLists = new CommandList[2];
        uint[] values = { firstValue, secondValue };

        for (int index = 0; index < commandLists.Length; index++)
        {
            buffers[index] = RF.CreateBuffer(new BufferDescription(
                sizeof(uint),
                BufferUsage.VertexBuffer));
            commandLists[index] = RF.CreateCommandList(new CommandListDescription
            {
                MaximumInFlightSubmissionCount = 1,
                InitialTrackedResourceCapacityPerSubmission = 2
            });
            commandLists[index].Begin();
            commandLists[index].UpdateBuffer(buffers[index], 0, values[index]);
            commandLists[index].End();
        }

        GD.SubmitCommands(commandLists[0]);

        VkGraphicsDevice vkGraphicsDevice = Assert.IsType<VkGraphicsDevice>(GD);
        VkCommandList firstCommandList = Assert.IsType<VkCommandList>(commandLists[0]);
        using var observer = new VulkanBlockingSubmissionFenceWaitObserver();
        vkGraphicsDevice.SubmissionFenceWaitObserver = observer;

        Task waitTask = Task.CompletedTask;
        Task submitTask = Task.CompletedTask;
        bool waitWasObserved = false;
        bool unrelatedSubmissionCompletedWhilePaused = false;
        try
        {
            waitTask = Task.Run(() =>
                vkGraphicsDevice.WaitForOldestSubmissionCompletion(firstCommandList));
            waitWasObserved = observer.WaitUntilEntered(TimeSpan.FromSeconds(10));
            if (waitWasObserved)
            {
                submitTask = Task.Run(() => GD.SubmitCommands(commandLists[1]));
                Task completedTask = await Task.WhenAny(
                    submitTask,
                    Task.Delay(TimeSpan.FromSeconds(10)));
                unrelatedSubmissionCompletedWhilePaused = ReferenceEquals(completedTask, submitTask);
            }
        }
        finally
        {
            observer.Release();
            try
            {
                await Task.WhenAll(waitTask, submitTask);
            }
            finally
            {
                vkGraphicsDevice.SubmissionFenceWaitObserver = null;
            }
        }

        Assert.True(waitWasObserved, "The bounded submission did not enter its fence-wait path.");
        Assert.True(
            unrelatedSubmissionCompletedWhilePaused,
            "An unrelated Vulkan submission was blocked by the bounded submission's fence wait.");

        GD.WaitForIdle();
        for (int index = 0; index < buffers.Length; index++)
        {
            DeviceBuffer readback = GetReadback(buffers[index]);
            MappedResourceView<uint> mapped = GD.Map<uint>(readback, MapMode.Read);
            Assert.Equal(values[index], mapped[0]);
            GD.Unmap(readback);
        }
    }

    [Fact]
    public void BoundedCommandListPreservesMultiPassRenderingAcrossSubmissions()
    {
        const uint width = 8;
        const uint height = 8;
        const int submissionCount = 16;

        Texture intermediate = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R32_G32_B32_A32_Float,
            TextureUsage.RenderTarget | TextureUsage.Sampled));
        Framebuffer intermediateFramebuffer = RF.CreateFramebuffer(
            new FramebufferDescription(null, intermediate));

        Texture output = RF.CreateTexture(TextureDescription.Texture2D(
            width,
            height,
            1,
            1,
            PixelFormat.R32_G32_B32_A32_Float,
            TextureUsage.RenderTarget));
        Framebuffer outputFramebuffer = RF.CreateFramebuffer(
            new FramebufferDescription(null, output));

        uint vertexSize = (uint)Unsafe.SizeOf<ColoredVertex>();
        DeviceBuffer vertexBuffer = RF.CreateBuffer(new BufferDescription(
            vertexSize * 4,
            BufferUsage.StructuredBufferReadWrite,
            vertexSize));

        ResourceLayout computeLayout = RF.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription(
                "OutputVertices",
                ResourceKind.StructuredBufferReadWrite,
                ShaderStages.Compute)));
        ResourceSet computeSet = RF.CreateResourceSet(
            new ResourceSetDescription(computeLayout, vertexBuffer));
        Pipeline computePipeline = RF.CreateComputePipeline(new ComputePipelineDescription(
            TestShaders.LoadCompute(RF, "ComputeColoredQuadGenerator"),
            computeLayout,
            1,
            1,
            1));

        ResourceLayout firstPassLayout = RF.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription(
                "InputVertices",
                ResourceKind.StructuredBufferReadOnly,
                ShaderStages.Vertex)));
        ResourceSet firstPassSet = RF.CreateResourceSet(
            new ResourceSetDescription(firstPassLayout, vertexBuffer));
        Pipeline firstPassPipeline = RF.CreateGraphicsPipeline(new GraphicsPipelineDescription(
            BlendStateDescription.SingleOverrideBlend,
            DepthStencilStateDescription.Disabled,
            RasterizerStateDescription.CullNone,
            PrimitiveTopology.TriangleStrip,
            new ShaderSetDescription(
                Array.Empty<VertexLayoutDescription>(),
                TestShaders.LoadVertexFragment(RF, "ColoredQuadRenderer")),
            firstPassLayout,
            intermediateFramebuffer.OutputDescription));

        ResourceLayout secondPassLayout = RF.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription(
                "Input",
                ResourceKind.TextureReadOnly,
                ShaderStages.Fragment),
            new ResourceLayoutElementDescription(
                "InputSampler",
                ResourceKind.Sampler,
                ShaderStages.Fragment)));
        ResourceSet secondPassSet = RF.CreateResourceSet(
            new ResourceSetDescription(secondPassLayout, intermediate, GD.PointSampler));
        RasterizerStateDescription secondPassRasterizer = RasterizerStateDescription.CullNone;
        secondPassRasterizer.ScissorTestEnabled = true;
        Pipeline secondPassPipeline = RF.CreateGraphicsPipeline(new GraphicsPipelineDescription(
            BlendStateDescription.SingleOverrideBlend,
            DepthStencilStateDescription.Disabled,
            secondPassRasterizer,
            PrimitiveTopology.TriangleStrip,
            new ShaderSetDescription(
                Array.Empty<VertexLayoutDescription>(),
                TestShaders.LoadVertexFragment(RF, "FullScreenBlit")),
            secondPassLayout,
            outputFramebuffer.OutputDescription));

        Texture[] captures = new Texture[submissionCount];
        for (int i = 0; i < captures.Length; i++)
        {
            captures[i] = RF.CreateTexture(TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                PixelFormat.R32_G32_B32_A32_Float,
                TextureUsage.Staging));
        }

        CommandList commandList = RF.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 2,
            InitialTrackedResourceCapacityPerSubmission = 16
        });

        for (int submissionIndex = 0; submissionIndex < submissionCount; submissionIndex++)
        {
            uint scissorX = submissionIndex % 2 == 0 ? 0u : width / 2;

            commandList.Begin();
            commandList.SetPipeline(computePipeline);
            commandList.SetComputeResourceSet(0, computeSet);
            commandList.Dispatch(1, 1, 1);

            commandList.SetFramebuffer(intermediateFramebuffer);
            commandList.ClearColorTarget(0, RgbaFloat.Black);
            commandList.SetPipeline(firstPassPipeline);
            commandList.SetGraphicsResourceSet(0, firstPassSet);
            commandList.Draw(4);

            commandList.SetFramebuffer(outputFramebuffer);
            commandList.ClearColorTarget(0, RgbaFloat.Blue);
            commandList.SetPipeline(secondPassPipeline);
            commandList.SetScissorRect(0, scissorX, 0, width / 2, height);
            commandList.SetGraphicsResourceSet(0, secondPassSet);
            commandList.Draw(4);
            commandList.CopyTexture(output, captures[submissionIndex]);
            commandList.End();

            GD.SubmitCommands(commandList);
        }

        GD.WaitForIdle();

        for (int submissionIndex = 0; submissionIndex < submissionCount; submissionIndex++)
        {
            MappedResourceView<RgbaFloat> capture = GD.Map<RgbaFloat>(
                captures[submissionIndex],
                MapMode.Read);
            RgbaFloat expectedLeft = submissionIndex % 2 == 0 ? RgbaFloat.Red : RgbaFloat.Blue;
            RgbaFloat expectedRight = submissionIndex % 2 == 0 ? RgbaFloat.Blue : RgbaFloat.Red;
            Assert.Equal(expectedLeft, capture[1, 4], RgbaFloatFuzzyComparer.Instance);
            Assert.Equal(expectedRight, capture[6, 4], RgbaFloatFuzzyComparer.Instance);
            GD.Unmap(captures[submissionIndex]);
        }
    }

    private static uint ExpectedValue(int workerIndex, uint update)
        => ((uint)workerIndex + 1u) * 100_000u + update;

    private static uint ExpectedSlotValue(int submissionIndex, int sliceIndex)
        => checked(
            10_000u +
            (uint)submissionIndex * 10_007u +
            (uint)sliceIndex * 97u);

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

    private static void AssertRecordingSlotUnavailable(CommandList commandList)
    {
        Assert.Throws<NeoVeldridException>(() =>
        {
            _ = commandList.RecordingSubmissionSlot;
        });
    }

}
#endif
