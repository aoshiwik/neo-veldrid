#if TEST_VULKAN
using System;
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
        using BlockingSubmissionFenceWaitObserver observer = new BlockingSubmissionFenceWaitObserver();
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

    private sealed class BlockingSubmissionFenceWaitObserver
        : VkGraphicsDevice.ISubmissionFenceWaitObserver, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);

        public void BeforeWait()
        {
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
}
#endif
