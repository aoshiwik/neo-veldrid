#if TEST_OPENGL || TEST_OPENGLES
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoVeldrid.OpenGL;
using Silk.NET.OpenGL;
using Xunit;

namespace NeoVeldrid.Tests;

public abstract class OpenGLDebugOutputTests<T> : GraphicsDeviceTestBase<T>
    where T : GraphicsDeviceCreator
{
    [Fact]
    public void RequestedDebugOutputIsProvedActive()
    {
        GraphicsDeviceValidationStatus status = GD.Validation.Status;
        BackendInfoOpenGL info = GD.GetOpenGLInfo();

        Assert.True(status.Requested);
        Assert.True(status.IsActive, status.ToString());
        Assert.True(status.HasFeature(GraphicsDeviceValidationFeatures.ApiDebugOutput));
        Assert.True(status.HasFeature(GraphicsDeviceValidationFeatures.SynchronousMessageDelivery));
        Assert.True(info.IsDebugContextStatusKnown);
        Assert.True(info.IsDebugContext);
        Assert.True(info.IsDebugOutputSynchronous);
        Assert.True(info.DebugOutputProbePassed);
        Assert.False(string.IsNullOrWhiteSpace(info.DebugOutputTransport));
        Assert.Equal(status.Transport, info.DebugOutputTransport);
    }

    [Fact]
    public void ActivationProbeWasCapturedByThisDevice()
    {
        GraphicsDeviceValidationMessage probe = Assert.Single(
            GD.Validation.GetMessageHistory(),
            message => message.Id == "0x4E56444C");

        Assert.Equal(GD.BackendType, probe.Backend);
        Assert.Equal(GraphicsDeviceValidationSeverity.Verbose, probe.Severity);
        Assert.Equal(nameof(DebugSource.DebugSourceApplication), probe.Source);
        Assert.Equal(nameof(DebugType.DebugTypeMarker), probe.Type);
        Assert.Equal(OpenGLDebugOutput.ProbeMessageText, probe.Text);
    }

    [Fact]
    public void HighSeverityNativeMessageFailsTheNextCheckpointOnce()
    {
        const uint messageId = 0x4E564845; // "NVHE"
        OpenGLGraphicsDevice gd = Assert.IsType<OpenGLGraphicsDevice>(GD);
        gd.InsertValidationTestMessage(
            DebugSeverity.DebugSeverityHigh,
            messageId,
            "NeoVeldrid validation gate test");

        GraphicsDeviceValidationException exception = Assert.Throws<GraphicsDeviceValidationException>(
            () => GD.CheckValidation("OpenGL debug-output test"));
        GraphicsDeviceValidationMessage message = Assert.Single(exception.Messages);
        Assert.Equal(GraphicsDeviceValidationSeverity.Error, message.Severity);
        Assert.Equal("0x4E564845", message.Id);

        Assert.Empty(GD.CheckValidation("OpenGL debug-output test second checkpoint"));
    }

    [Theory]
    [InlineData((uint)DebugSeverity.DebugSeverityHigh, GraphicsDeviceValidationSeverity.Error)]
    [InlineData((uint)DebugSeverity.DebugSeverityMedium, GraphicsDeviceValidationSeverity.Warning)]
    [InlineData((uint)DebugSeverity.DebugSeverityLow, GraphicsDeviceValidationSeverity.Information)]
    [InlineData((uint)DebugSeverity.DebugSeverityNotification, GraphicsDeviceValidationSeverity.Verbose)]
    [InlineData(uint.MaxValue, GraphicsDeviceValidationSeverity.Corruption)]
    public void NativeSeverityMappingIsExplicit(
        uint nativeSeverity,
        GraphicsDeviceValidationSeverity expected)
    {
        Assert.Equal(expected, OpenGLDebugOutput.MapSeverity(nativeSeverity));
    }

    [Theory]
    [InlineData((uint)DebugType.DebugTypeError)]
    [InlineData((uint)DebugType.DebugTypeUndefinedBehavior)]
    public void NativeFailureTypesCannotBeDowngradedByDriverSeverity(uint nativeType)
    {
        Assert.Equal(
            GraphicsDeviceValidationSeverity.Error,
            OpenGLDebugOutput.MapSeverity(
                nativeType,
                (uint)DebugSeverity.DebugSeverityNotification));
    }

    [Fact(Timeout = 10000)]
    public Task ExecutionThreadAllowsNestedSynchronousOperations()
    {
        return Task.Run(() =>
        {
            BackendInfoOpenGL info = GD.GetOpenGLInfo();
            info.ExecuteOnGLThread(() =>
            {
                GD.WaitForIdle();
                info.FlushAndFinish();
            });
        });
    }

    [Fact(Timeout = 10000)]
    public Task ExecutionThreadActionPropagatesItsExactException()
    {
        return Task.Run(() =>
        {
            BackendInfoOpenGL info = GD.GetOpenGLInfo();
            InvalidOperationException sentinel = new InvalidOperationException(
                "OpenGL execution-thread exception sentinel");

            InvalidOperationException observed = Assert.Throws<InvalidOperationException>(
                () => info.ExecuteOnGLThread(() => throw sentinel));

            Assert.Same(sentinel, observed);
        });
    }

    [Fact(Timeout = 10000)]
    public Task ValidationBoundaryQueuedBehindExecutionThreadActionDoesNotDeadlock()
    {
        return Task.Run(() =>
        {
            BackendInfoOpenGL info = GD.GetOpenGLInfo();
            using ManualResetEventSlim actionEntered = new ManualResetEventSlim();
            using ManualResetEventSlim externalBoundaryEntered = new ManualResetEventSlim();

            Task action = Task.Run(() => info.ExecuteOnGLThread(() =>
            {
                actionEntered.Set();
                Assert.True(SpinWait.SpinUntil(
                    () => GD.PendingBackendWorkItemCount != 0,
                    5000));
                GD.WaitForIdle();
                Assert.False(externalBoundaryEntered.IsSet);
            }));

            Assert.True(actionEntered.Wait(5000));
            Task externalBoundary = Task.Run(() => info.ExecuteOnGLThread(() =>
            {
                externalBoundaryEntered.Set();
                GD.WaitForIdle();
            }));

            Assert.True(Task.WaitAll(new[] { action, externalBoundary }, 5000));
            Assert.True(externalBoundaryEntered.IsSet);
        });
    }
}

public sealed class OpenGLPlatformInfoTests
{
    [Fact]
    public void OwnedContextHandleMustBeNonzero()
    {
        Assert.Throws<ArgumentException>(() => new OpenGLPlatformInfo(
            IntPtr.Zero,
            _ => IntPtr.Zero,
            _ => { },
            () => IntPtr.Zero,
            () => { },
            _ => { },
            () => { },
            _ => { }));
    }
}

public sealed class OpenGLFenceLifecycleTests
{
    [Fact]
    public void ResetIsRejectedWhileSubmissionIsPending()
    {
        using OpenGLFence fence = new OpenGLFence(signaled: false);
        fence.BeginSubmission();

        Assert.Throws<InvalidOperationException>(fence.Reset);

        fence.CompleteSubmission(exception: null);
    }

    [Fact]
    public void DisposeDuringSubmissionDefersEventReleaseUntilCompletion()
    {
        OpenGLFence fence = new OpenGLFence(signaled: false);
        fence.BeginSubmission();

        fence.Dispose();
        fence.CompleteSubmission(exception: null);

        Assert.True(fence.IsDisposed);
    }

    [Fact]
    public void WaitPropagatesTheExactSubmissionFailure()
    {
        using OpenGLFence fence = new OpenGLFence(signaled: false);
        InvalidOperationException sentinel = new InvalidOperationException(
            "OpenGL fence exception sentinel");
        fence.BeginSubmission();
        fence.CompleteSubmission(sentinel);

        InvalidOperationException observed = Assert.Throws<InvalidOperationException>(
            () => fence.Wait(0));

        Assert.Same(sentinel, observed);
    }
}

#if TEST_OPENGL
[Trait("Backend", "OpenGL")]
public sealed class OpenGLDebugOutputTests : OpenGLDebugOutputTests<OpenGLDeviceCreator>
{
}
#endif

#if TEST_OPENGLES
[Trait("Backend", "OpenGLES")]
public sealed class OpenGLESDebugOutputTests : OpenGLDebugOutputTests<OpenGLESDeviceCreator>
{
}
#endif
#endif
