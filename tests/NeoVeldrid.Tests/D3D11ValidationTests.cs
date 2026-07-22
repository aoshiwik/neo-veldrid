#if TEST_D3D11
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "D3D11")]
public sealed class D3D11DeviceCreationOptionTests
{
    [Fact]
    public void NonDebugRequestDoesNotCreateDebugDeviceInDebugBuild()
    {
        using GraphicsDevice device = GraphicsDevice.CreateD3D11(
            new GraphicsDeviceOptions(debug: false));

        Assert.False(device.IsDebugRequested);
        Assert.True(device.GetD3D11Info(out BackendInfoD3D11 info));
        Assert.False(info.DebugLayerWasProbed);
        Assert.False(info.DebugDeviceWasCreated);
        Assert.False(info.ValidationInfoQueueWasActivated);
    }

    [Fact]
    public void ConcurrentCommandListDisposalReleasesItsContextOnce()
    {
        using GraphicsDevice device = GraphicsDevice.CreateD3D11(
            new GraphicsDeviceOptions(debug: false));
        CommandList commandList = device.ResourceFactory.CreateCommandList();

        Parallel.For(0, 16, _ => commandList.Dispose());

        Assert.True(commandList.IsDisposed);
    }
}

[Trait("Backend", "D3D11")]
public sealed class D3D11CommandListDisposalTests
    : GraphicsDeviceTestBase<D3D11DeviceCreatorWithMainSwapchain>
{
    [Fact]
    public void DisposeWhileRecordingReleasesSwapchainAndStagingOwnership()
    {
        using DeviceBuffer destination = RF.CreateBuffer(new BufferDescription(
            512,
            BufferUsage.UniformBuffer));
        CommandList commandList = RF.CreateCommandList();

        commandList.Begin();
        commandList.SetFramebuffer(GD.MainSwapchain.Framebuffer);
        commandList.UpdateBuffer(destination, 16, 0x1234ABCDu);
        commandList.Dispose();

        GD.MainSwapchain.Resize(128, 128);

        Assert.True(commandList.IsDisposed);
        GD.CheckValidation("disposed in-progress D3D11 command list");
    }
}

[Trait("Backend", "D3D11")]
public unsafe class D3D11ValidationTests : GraphicsDeviceTestBase<D3D11DeviceCreator>
{
    [Fact]
    public void WaitForIdleCompletesEarlierNativeEventQuery()
    {
        Assert.True(GD.GetD3D11Info(out BackendInfoD3D11 info));

        ID3D11Device* device = (ID3D11Device*)info.Device;
        ID3D11DeviceContext* context = null;
        ID3D11Query* completionQuery = null;
        try
        {
            device->GetImmediateContext(&context);
            Assert.True(context != null);

            QueryDesc queryDescription = new QueryDesc
            {
                Query = Silk.NET.Direct3D11.Query.Event,
                MiscFlags = 0,
            };
            SilkMarshal.ThrowHResult(device->CreateQuery(&queryDescription, &completionQuery));

            // Do not flush here. WaitForIdle must submit its own later event
            // marker and cannot return until this earlier marker is complete.
            context->End((ID3D11Asynchronous*)completionQuery);
            GD.WaitForIdle();

            int result = context->GetData(
                (ID3D11Asynchronous*)completionQuery,
                null,
                0,
                (uint)AsyncGetdataFlag.Donotflush);
            if (result < 0)
            {
                SilkMarshal.ThrowHResult(result);
            }
            Assert.Equal(0, result); // S_OK, not S_FALSE.
        }
        finally
        {
            if (completionQuery != null)
            {
                completionQuery->Release();
            }
            if (context != null)
            {
                context->Release();
            }
        }
    }

    [Fact]
    public void FenceWaitCompletesEarlierNativeEventQuery()
    {
        Assert.True(GD.GetD3D11Info(out BackendInfoD3D11 info));

        using CommandList commandList = RF.CreateCommandList();
        using Fence fence = RF.CreateFence(false);
        commandList.Begin();
        commandList.End();

        ID3D11Device* device = (ID3D11Device*)info.Device;
        ID3D11DeviceContext* context = null;
        ID3D11Query* earlierQuery = null;
        try
        {
            device->GetImmediateContext(&context);
            Assert.True(context != null);

            QueryDesc queryDescription = new QueryDesc
            {
                Query = Silk.NET.Direct3D11.Query.Event,
                MiscFlags = 0,
            };
            SilkMarshal.ThrowHResult(device->CreateQuery(&queryDescription, &earlierQuery));

            context->End((ID3D11Asynchronous*)earlierQuery);
            GD.SubmitCommands(commandList, fence);
            Assert.True(GD.WaitForFence(fence, TimeSpan.FromSeconds(5)));

            int result = context->GetData(
                (ID3D11Asynchronous*)earlierQuery,
                null,
                0,
                (uint)AsyncGetdataFlag.Donotflush);
            if (result < 0)
            {
                SilkMarshal.ThrowHResult(result);
            }
            Assert.Equal(0, result); // The fence marker follows this event.
        }
        finally
        {
            if (earlierQuery != null)
            {
                earlierQuery->Release();
            }
            if (context != null)
            {
                context->Release();
            }
        }
    }

    [Fact]
    public void FenceCanResetAfterDeviceIdleWithoutPriorFencePoll()
    {
        using CommandList commandList = RF.CreateCommandList();
        using Fence fence = RF.CreateFence(false);

        commandList.Begin();
        commandList.End();
        GD.SubmitCommands(commandList, fence);
        GD.WaitForIdle();

        // WaitForIdle proves the query complete even though this fence has not
        // itself been polled yet. Reset must observe that native completion.
        GD.ResetFence(fence);
        Assert.False(fence.Signaled);

        commandList.Begin();
        commandList.End();
        GD.SubmitCommands(commandList, fence);
        Assert.True(GD.WaitForFence(fence, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ForeignDeviceFenceIsRejectedBeforeEveryNativeFenceOperation()
    {
        using GraphicsDevice otherDevice = TestUtils.CreateD3D11Device();
        using Fence foreignFence = otherDevice.ResourceFactory.CreateFence(false);
        using CommandList commandList = RF.CreateCommandList();
        commandList.Begin();
        commandList.End();

        Assert.Throws<InvalidOperationException>(() =>
            GD.SubmitCommands(commandList, foreignFence));
        Assert.Throws<InvalidOperationException>(() =>
            GD.WaitForFence(foreignFence, 0));
        Assert.Throws<InvalidOperationException>(() =>
            GD.WaitForFences(
                new[] { foreignFence },
                waitAll: true,
                nanosecondTimeout: 0));
        Assert.Throws<InvalidOperationException>(() =>
            GD.ResetFence(foreignFence));

        // The rejected fenced submission must not consume or execute the
        // command list before ownership validation completes.
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();
    }

    [Fact]
    public void ActivationEvidenceMatchesNativeProbeAndInfoQueue()
    {
        Assert.True(GD.GetD3D11Info(out BackendInfoD3D11 info));
        Assert.True(GD.IsDebugRequested);
        Assert.True(info.DebugLayerWasProbed);
        Assert.Equal(info.DebugLayerProbeSucceeded, info.DebugDeviceWasCreated);
        Assert.Equal(info.ValidationInfoQueueWasActivated, GD.IsDebugActive);
        Assert.Equal(GD.Validation.Status, info.ValidationStatus);

        if (Environment.GetEnvironmentVariable("NEOVELDRID_REQUIRE_D3D11_DEBUG") == "1")
        {
            Assert.True(
                info.DebugLayerProbeSucceeded,
                $"D3D11 validation was required, but the SDK-layer probe returned "
                + $"0x{info.DebugLayerProbeHResult.GetValueOrDefault():X8}.");
            Assert.True(info.DebugDeviceWasCreated);
            Assert.True(
                info.ValidationInfoQueueWasActivated,
                info.ValidationStatus.InactiveReason);
            Assert.True(
                info.ValidationStatus.HasFeature(GraphicsDeviceValidationFeatures.LiveObjectTracking),
                info.ValidationStatus.ToString());
        }
    }

    [SkippableFact]
    public void InfoQueueMessagesAreCopiedOnceWithNormalizedSeverity()
    {
        Assert.True(GD.GetD3D11Info(out BackendInfoD3D11 info));
        Skip.IfNot(
            info.ValidationInfoQueueWasActivated,
            $"NV-SKIP-D3D11-INFO-QUEUE: {info.ValidationStatus.InactiveReason}");

        GD.CheckValidation("D3D11 validation test precondition");
        ulong collectedBefore = info.CollectedValidationMessageCount;
        const string marker = "NeoVeldrid D3D11 InfoQueue warning sentinel";

        ID3D11InfoQueue* queue = QueryInfoQueue(info.Device);
        try
        {
            SilkMarshal.ThrowHResult(
                queue->AddApplicationMessage(MessageSeverity.Warning, marker));
        }
        finally
        {
            queue->Release();
        }

        IReadOnlyList<GraphicsDeviceValidationMessage> observed =
            GD.CheckValidation("D3D11 validation warning sentinel");
        GraphicsDeviceValidationMessage message = Assert.Single(
            observed,
            candidate => candidate.Text.Contains(marker, StringComparison.Ordinal));
        Assert.Equal(GraphicsDeviceValidationSeverity.Warning, message.Severity);
        Assert.Equal("D3D11", message.Source);
        Assert.Equal(collectedBefore + 1, info.CollectedValidationMessageCount);

        Assert.Empty(GD.CheckValidation("D3D11 validation warning sentinel replay"));
    }

    [SkippableFact]
    public void ErrorMessageFailsTheOwningCheckpoint()
    {
        Assert.True(GD.GetD3D11Info(out BackendInfoD3D11 info));
        Skip.IfNot(
            info.ValidationInfoQueueWasActivated,
            $"NV-SKIP-D3D11-INFO-QUEUE: {info.ValidationStatus.InactiveReason}");

        GD.CheckValidation("D3D11 validation error test precondition");
        const string marker = "NeoVeldrid D3D11 InfoQueue error sentinel";

        ID3D11InfoQueue* queue = QueryInfoQueue(info.Device);
        try
        {
            SilkMarshal.ThrowHResult(
                queue->AddApplicationMessage(MessageSeverity.Error, marker));
        }
        finally
        {
            queue->Release();
        }

        GraphicsDeviceValidationException exception = Assert.Throws<GraphicsDeviceValidationException>(
            () => GD.CheckValidation("D3D11 validation error sentinel"));
        Assert.Contains(
            exception.Messages,
            message => message.Severity == GraphicsDeviceValidationSeverity.Error
                && message.Text.Contains(marker, StringComparison.Ordinal));

        // A checkpoint owns each message exactly once, including failed ones.
        Assert.Empty(GD.CheckValidation("D3D11 validation error sentinel replay"));
    }

    [SkippableFact]
    public void DiscardedNativeMessagesFailTheOwningCheckpoint()
    {
        Assert.True(GD.GetD3D11Info(out BackendInfoD3D11 info));
        Skip.IfNot(
            info.ValidationInfoQueueWasActivated,
            $"NV-SKIP-D3D11-INFO-QUEUE: {info.ValidationStatus.InactiveReason}");

        GD.CheckValidation("D3D11 discarded-message test precondition");

        ID3D11InfoQueue* queue = QueryInfoQueue(info.Device);
        try
        {
            ulong storedMessageCount = queue->GetNumStoredMessages();
            SilkMarshal.ThrowHResult(queue->SetMessageCountLimit(storedMessageCount + 1));
            SilkMarshal.ThrowHResult(queue->AddApplicationMessage(
                MessageSeverity.Warning,
                "NeoVeldrid D3D11 queue-capacity sentinel one"));
            SilkMarshal.ThrowHResult(queue->AddApplicationMessage(
                MessageSeverity.Warning,
                "NeoVeldrid D3D11 queue-capacity sentinel two"));
        }
        finally
        {
            queue->Release();
        }

        GraphicsDeviceValidationException exception = Assert.Throws<GraphicsDeviceValidationException>(
            () => GD.CheckValidation("D3D11 discarded-message sentinel"));
        Assert.Contains(
            exception.Messages,
            message => message.Id == "MessagesDiscarded");
        Assert.True(info.DiscardedValidationMessageCount > 0);

        queue = QueryInfoQueue(info.Device);
        try
        {
            SilkMarshal.ThrowHResult(queue->SetMessageCountLimit(65_536));
        }
        finally
        {
            queue->Release();
        }

        Assert.Empty(GD.CheckValidation("D3D11 discarded-message sentinel replay"));
    }

    private static ID3D11InfoQueue* QueryInfoQueue(IntPtr devicePointer)
    {
        ID3D11InfoQueue* queue = null;
        Guid infoQueueGuid = ID3D11InfoQueue.Guid;
        SilkMarshal.ThrowHResult(
            ((IUnknown*)devicePointer)->QueryInterface(&infoQueueGuid, (void**)&queue));
        return queue;
    }
}

[Trait("Backend", "D3D11")]
public unsafe class D3D11LiveObjectValidationTests
{
    [SkippableFact]
    public void UndisposedBufferFailsTeardownLiveObjectGate()
    {
        GraphicsDevice gd = TestUtils.CreateD3D11Device();
        DeviceBuffer leakedBuffer = null;
        try
        {
            Assert.True(gd.GetD3D11Info(out BackendInfoD3D11 info));
            Skip.IfNot(
                info.ValidationStatus.HasFeature(GraphicsDeviceValidationFeatures.LiveObjectTracking),
                $"NV-SKIP-D3D11-LIVE-OBJECTS: {info.ValidationStatus.InactiveReason}");

            leakedBuffer = gd.ResourceFactory.CreateBuffer(
                new BufferDescription(64, BufferUsage.VertexBuffer));
            leakedBuffer.Name = "Intentional D3D11 live-object validation sentinel";

            GraphicsDeviceValidationException exception =
                Assert.Throws<GraphicsDeviceValidationException>(gd.Dispose);
            Assert.Contains(
                exception.Messages,
                message => message.Type == "LiveObject"
                    && message.Id.Contains("LiveBuffer", StringComparison.Ordinal));
        }
        finally
        {
            leakedBuffer?.Dispose();
            gd.Dispose();
        }
    }
}
#endif
