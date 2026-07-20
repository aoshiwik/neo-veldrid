using System;
using System.Threading;
using System.Threading.Tasks;
using NeoVeldrid.Sdl2;
using NeoVeldrid.StartupUtilities;
using Xunit;

namespace NeoVeldrid.Tests;

// Regression tests for disposal bugs that have been fixed in NeoVeldrid. Each test guards a
// specific past bug and is not part of the general disposal coverage in DisposalTests.cs.
//
// Unlike DisposalTestBase<T>, these tests do not use the shared fixture device, because they
// need to dispose the GraphicsDevice itself: the fixture teardown calls WaitForIdle() before
// its own Dispose(), which would hit the same hang the test below exercises. Each test creates
// and owns a throwaway device instead.
public abstract class DeviceDisposalRegressionTests<T> where T : GraphicsDeviceCreator
{
    // GraphicsDevice.Dispose() used to re-run PlatformDispose() on every call. On the OpenGL
    // backend the second call posted a FlushAndFinish work item to the execution thread, but
    // that thread had already terminated during the first dispose, so the post blocked forever.
    // IDisposable requires Dispose to be safe to call more than once; this verifies the second
    // call returns promptly instead of deadlocking. The timeout turns a regression into a test
    // failure rather than a hung run. It also checks IsDisposed tracks the lifecycle: false
    // while the device is live, true once disposed.
    [Fact(Timeout = 10000)]
    public Task Dispose_Twice_DoesNotHang()
    {
        // The work runs on a separate thread so the timeout can fire even if the second
        // Dispose() blocks: a synchronous deadlock would freeze the very thread the test
        // runner waits on, leaving nothing to time out.
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(out Sdl2Window window, out GraphicsDevice gd);
            Assert.False(gd.IsDisposed);
            gd.Dispose();
            gd.Dispose();
            Assert.True(gd.IsDisposed);
            window?.Close();
        });
    }

    [Fact(Timeout = 10000)]
    public Task Dispose_DrainsDeferredAdditionsBeforeClosingAdmission()
    {
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(out Sdl2Window window, out GraphicsDevice gd);
            try
            {
                int disposalCount = 0;
                CallbackDisposable second = new CallbackDisposable(() => disposalCount++);
                Assert.Throws<ArgumentNullException>(() => gd.DisposeWhenIdle(null));
                gd.DisposeWhenIdle(new CallbackDisposable(() =>
                {
                    disposalCount++;
                    gd.DisposeWhenIdle(second);
                }));

                gd.Dispose();

                Assert.Equal(2, disposalCount);
                Assert.Throws<ObjectDisposedException>(() =>
                    gd.DisposeWhenIdle(new CallbackDisposable(() => { })));
            }
            finally
            {
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task Dispose_ClosingRejectsExternalEnqueueAndConcurrentDisposeJoinsOwner()
    {
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(out Sdl2Window window, out GraphicsDevice gd);
            using ManualResetEventSlim finalDrainEntered = new ManualResetEventSlim();
            using ManualResetEventSlim releaseFinalDrain = new ManualResetEventSlim();
            using ManualResetEventSlim concurrentDisposeStarted = new ManualResetEventSlim();
            Task owningDispose = null;
            Task concurrentDispose = null;
            try
            {
                gd.DisposeWhenIdle(new CallbackDisposable(() =>
                {
                    finalDrainEntered.Set();
                    releaseFinalDrain.Wait();
                }));

                owningDispose = Task.Run(gd.Dispose);
                Assert.True(finalDrainEntered.Wait(5000));

                // Closing accepts additions made reentrantly by its owning
                // callback, but unrelated producers must not be able to keep
                // final teardown alive indefinitely.
                Assert.Throws<ObjectDisposedException>(() =>
                    gd.DisposeWhenIdle(new CallbackDisposable(() => { })));

                concurrentDispose = Task.Run(() =>
                {
                    concurrentDisposeStarted.Set();
                    gd.Dispose();
                });
                Assert.True(concurrentDisposeStarted.Wait(5000));
                Assert.False(concurrentDispose.Wait(100));

                releaseFinalDrain.Set();
                Assert.True(Task.WaitAll(new[] { owningDispose, concurrentDispose }, 5000));
                Assert.True(gd.IsDisposed);
            }
            finally
            {
                releaseFinalDrain.Set();
                owningDispose?.Wait(5000);
                concurrentDispose?.Wait(5000);
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task WaitForIdle_ReentrantDeviceDisposeFailsWithoutDeadlockingOrClosingDevice()
    {
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(out Sdl2Window window, out GraphicsDevice gd);
            try
            {
                gd.DisposeWhenIdle(new CallbackDisposable(gd.Dispose));

                InvalidOperationException exception =
                    Assert.Throws<InvalidOperationException>(gd.WaitForIdle);

                Assert.Contains("reentrantly", exception.Message, StringComparison.Ordinal);
                Assert.False(gd.IsDisposed);
            }
            finally
            {
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task DeferredDrainReentrantDisposeCannotJoinConcurrentTeardown()
    {
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(out Sdl2Window window, out GraphicsDevice gd);
            using ManualResetEventSlim callbackEntered = new ManualResetEventSlim();
            Task idleDrain = null;
            Task concurrentDispose = null;
            try
            {
                gd.DisposeWhenIdle(new CallbackDisposable(() =>
                {
                    callbackEntered.Set();
                    bool disposalDependencyObserved;
                    if (gd.BackendType == GraphicsBackend.OpenGL
                        || gd.BackendType == GraphicsBackend.OpenGLES)
                    {
                        // OpenGL gives the execution thread ownership of teardown,
                        // so the external request is visible as queued work before
                        // the shared disposal transaction itself can begin.
                        disposalDependencyObserved = SpinWait.SpinUntil(
                            () => gd.PendingBackendWorkItemCount != 0,
                            5000);
                    }
                    else
                    {
                        disposalDependencyObserved = SpinWait.SpinUntil(
                            () => gd.IsDisposed,
                            5000);
                    }

                    Assert.True(disposalDependencyObserved);
                    Assert.Throws<InvalidOperationException>(gd.Dispose);
                }));

                idleDrain = Task.Run(gd.WaitForIdle);
                Assert.True(callbackEntered.Wait(5000));
                concurrentDispose = Task.Run(gd.Dispose);

                Assert.True(Task.WaitAll(new[] { idleDrain, concurrentDispose }, 5000));
                Assert.True(gd.IsDisposed);
            }
            finally
            {
                idleDrain?.Wait(5000);
                concurrentDispose?.Wait(5000);
                gd.Dispose();
                window?.Close();
            }
        });
    }

    protected sealed class CallbackDisposable : IDisposable
    {
        private readonly Action _callback;

        public CallbackDisposable(Action callback)
        {
            _callback = callback;
        }

        public void Dispose() => _callback();
    }
}

#if TEST_VULKAN
[Trait("Backend", "Vulkan")]
public class VulkanDeviceDisposalRegressionTests : DeviceDisposalRegressionTests<VulkanDeviceCreator> { }
#endif
#if TEST_D3D11
[Trait("Backend", "D3D11")]
public class D3D11DeviceDisposalRegressionTests : DeviceDisposalRegressionTests<D3D11DeviceCreator> { }
#endif
#if TEST_OPENGL || TEST_OPENGLES
public abstract class OpenGLDeviceDisposalRegressionTests<T> : DeviceDisposalRegressionTests<T>
    where T : GraphicsDeviceCreator
{
    [Fact(Timeout = 10000)]
    public Task Dispose_FromExecutionThreadDoesNotDeadlock()
    {
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(
                out Sdl2Window window,
                out GraphicsDevice gd);
            try
            {
                gd.GetOpenGLInfo().ExecuteOnGLThread(gd.Dispose);
                Assert.True(gd.IsDisposed);
            }
            finally
            {
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task ConcurrentExternalDisposeAndExecutionThreadDisposeDoNotDeadlock()
    {
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(
                out Sdl2Window window,
                out GraphicsDevice gd);
            Task executionThreadDispose = null;
            Task externalDispose = null;
            using ManualResetEventSlim actionEntered = new ManualResetEventSlim();
            using ManualResetEventSlim teardownPaused = new ManualResetEventSlim();
            using ManualResetEventSlim releaseTeardown = new ManualResetEventSlim();
            try
            {
                BackendInfoOpenGL info = gd.GetOpenGLInfo();
                gd.DisposeWhenIdle(new CallbackDisposable(() =>
                {
                    teardownPaused.Set();
                    releaseTeardown.Wait();
                }));
                executionThreadDispose = Task.Run(() => info.ExecuteOnGLThread(() =>
                {
                    actionEntered.Set();
                    Assert.True(SpinWait.SpinUntil(
                        () => gd.PendingBackendWorkItemCount != 0,
                        5000));
                    gd.Dispose();
                    Assert.True(externalDispose.Wait(5000));
                }));

                Assert.True(actionEntered.Wait(5000));
                externalDispose = Task.Run(gd.Dispose);

                Assert.True(teardownPaused.Wait(5000));
                Assert.False(externalDispose.Wait(100));
                releaseTeardown.Set();
                Assert.True(Task.WaitAll(
                    new[] { executionThreadDispose, externalDispose },
                    5000));
                Assert.True(gd.IsDisposed);
            }
            finally
            {
                releaseTeardown.Set();
                executionThreadDispose?.Wait(5000);
                externalDispose?.Wait(5000);
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task ExecutionThreadDisposeRejectsQueuedUnrelatedTransaction()
    {
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(
                out Sdl2Window window,
                out GraphicsDevice gd);
            Task owningDispose = null;
            Task queuedTransaction = null;
            using ManualResetEventSlim ownerEntered = new ManualResetEventSlim();
            using ManualResetEventSlim queuedDelegateEntered = new ManualResetEventSlim();
            using ManualResetEventSlim releaseQueuedDelegate = new ManualResetEventSlim();
            try
            {
                BackendInfoOpenGL info = gd.GetOpenGLInfo();
                owningDispose = Task.Run(() => info.ExecuteOnGLThread(() =>
                {
                    ownerEntered.Set();
                    Assert.True(SpinWait.SpinUntil(
                        () => gd.PendingBackendWorkItemCount != 0,
                        5000));
                    gd.Dispose();
                }));

                Assert.True(ownerEntered.Wait(5000));
                queuedTransaction = Task.Run(() => info.ExecuteOnGLThread(() =>
                {
                    queuedDelegateEntered.Set();
                    releaseQueuedDelegate.Wait();
                }));

                Assert.True(owningDispose.Wait(5000));
                Assert.False(queuedDelegateEntered.IsSet);
                Assert.Throws<ObjectDisposedException>(
                    () => queuedTransaction.GetAwaiter().GetResult());
                Assert.True(gd.IsDisposed);
            }
            finally
            {
                releaseQueuedDelegate.Set();
                owningDispose?.Wait(5000);
                try
                {
                    queuedTransaction?.Wait(5000);
                }
                catch (AggregateException)
                {
                }
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task DeferredDrainNestedIdleCannotExecuteQueuedDispose()
    {
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(
                out Sdl2Window window,
                out GraphicsDevice gd);
            Task idleDrain = null;
            Task externalDispose = null;
            using ManualResetEventSlim callbackEntered = new ManualResetEventSlim();
            try
            {
                gd.DisposeWhenIdle(new CallbackDisposable(() =>
                {
                    callbackEntered.Set();
                    Assert.True(SpinWait.SpinUntil(
                        () => gd.PendingBackendWorkItemCount != 0,
                        5000));

                    // This nested boundary must remain part of the active GL
                    // transaction. Pumping the later disposal request here
                    // would misclassify its physical GL thread as deferred-drain
                    // reentry and could destroy the context under this callback.
                    gd.WaitForIdle();
                }));

                idleDrain = Task.Run(gd.WaitForIdle);
                Assert.True(callbackEntered.Wait(5000));
                externalDispose = Task.Run(gd.Dispose);

                Assert.True(Task.WaitAll(new[] { idleDrain, externalDispose }, 5000));
                Assert.True(gd.IsDisposed);
            }
            finally
            {
                idleDrain?.Wait(5000);
                externalDispose?.Wait(5000);
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task NestedIdleIncludesCommandsSubmittedBeforeTheCall()
    {
        return Task.Run(() =>
        {
            CreateNonDebugGraphicsDevice(out Sdl2Window window, out GraphicsDevice gd);
            CommandList commandList = null;
            Fence fence = null;
            Task executionThreadAction = null;
            using ManualResetEventSlim actionEntered = new ManualResetEventSlim();
            using ManualResetEventSlim submissionReturned = new ManualResetEventSlim();
            try
            {
                commandList = gd.ResourceFactory.CreateCommandList();
                fence = gd.ResourceFactory.CreateFence(signaled: false);
                commandList.Begin();
                commandList.End();

                BackendInfoOpenGL info = gd.GetOpenGLInfo();
                executionThreadAction = Task.Run(() => info.ExecuteOnGLThread(() =>
                {
                    actionEntered.Set();
                    Assert.True(submissionReturned.Wait(5000));

                    // The submitted command is already admitted behind this
                    // synchronous action. A nested idle marker may defer later
                    // API transactions, but it must still execute earlier GPU
                    // submissions before returning.
                    gd.WaitForIdle();
                    Assert.True(fence.Signaled);
                }));

                Assert.True(actionEntered.Wait(5000));
                gd.SubmitCommands(commandList, fence);
                submissionReturned.Set();

                Assert.True(executionThreadAction.Wait(5000));
            }
            finally
            {
                submissionReturned.Set();
                executionThreadAction?.Wait(5000);
                commandList?.Dispose();
                fence?.Dispose();
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task NestedIdleDoesNotOvertakeQueuedTransactionToReachSubmittedCommands()
    {
        return Task.Run(() =>
        {
            CreateNonDebugGraphicsDevice(out Sdl2Window window, out GraphicsDevice gd);
            CommandList commandList = null;
            Fence fence = null;
            Task executionThreadAction = null;
            Task queuedTransaction = null;
            using ManualResetEventSlim actionEntered = new ManualResetEventSlim();
            using ManualResetEventSlim submissionReturned = new ManualResetEventSlim();
            using ManualResetEventSlim queuedDelegateEntered = new ManualResetEventSlim();
            using ManualResetEventSlim releaseQueuedDelegate = new ManualResetEventSlim();
            try
            {
                commandList = gd.ResourceFactory.CreateCommandList();
                fence = gd.ResourceFactory.CreateFence(signaled: false);
                commandList.Begin();
                commandList.End();

                BackendInfoOpenGL info = gd.GetOpenGLInfo();
                executionThreadAction = Task.Run(() => info.ExecuteOnGLThread(() =>
                {
                    actionEntered.Set();
                    Assert.True(submissionReturned.Wait(5000));

                    InvalidOperationException exception =
                        Assert.Throws<InvalidOperationException>(gd.WaitForIdle);
                    Assert.Contains(
                        "separates it from submitted native work",
                        exception.Message,
                        StringComparison.Ordinal);
                    Assert.False(queuedDelegateEntered.IsSet);
                    Assert.False(fence.Signaled);

                    InvalidOperationException retryException =
                        Assert.Throws<InvalidOperationException>(gd.WaitForIdle);
                    Assert.Contains(
                        "separates it from submitted native work",
                        retryException.Message,
                        StringComparison.Ordinal);
                    Assert.False(queuedDelegateEntered.IsSet);
                    Assert.False(fence.Signaled);
                }));

                Assert.True(actionEntered.Wait(5000));
                queuedTransaction = Task.Run(() => info.ExecuteOnGLThread(() =>
                {
                    queuedDelegateEntered.Set();
                    releaseQueuedDelegate.Wait();
                    gd.WaitForIdle();
                    Assert.True(fence.Signaled);
                }));
                Assert.True(SpinWait.SpinUntil(
                    () => gd.PendingBackendWorkItemCount != 0,
                    5000));

                gd.SubmitCommands(commandList, fence);
                submissionReturned.Set();

                Assert.True(executionThreadAction.Wait(5000));
                Assert.True(queuedDelegateEntered.Wait(5000));
                releaseQueuedDelegate.Set();
                Assert.True(queuedTransaction.Wait(5000));

                gd.WaitForIdle();
                Assert.True(fence.Signaled);
            }
            finally
            {
                submissionReturned.Set();
                releaseQueuedDelegate.Set();
                executionThreadAction?.Wait(5000);
                queuedTransaction?.Wait(5000);
                commandList?.Dispose();
                fence?.Dispose();
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task RepeatedNestedIdlePreservesPreviouslyDeferredTransactionBarrier()
    {
        return Task.Run(() =>
        {
            CreateNonDebugGraphicsDevice(out Sdl2Window window, out GraphicsDevice gd);
            CommandList commandList = null;
            Fence fence = null;
            Task executionThreadAction = null;
            Task queuedTransaction = null;
            using ManualResetEventSlim actionEntered = new ManualResetEventSlim();
            using ManualResetEventSlim firstBoundaryCompleted = new ManualResetEventSlim();
            using ManualResetEventSlim submissionReturned = new ManualResetEventSlim();
            using ManualResetEventSlim queuedDelegateEntered = new ManualResetEventSlim();
            using ManualResetEventSlim releaseQueuedDelegate = new ManualResetEventSlim();
            try
            {
                commandList = gd.ResourceFactory.CreateCommandList();
                fence = gd.ResourceFactory.CreateFence(signaled: false);
                commandList.Begin();
                commandList.End();

                BackendInfoOpenGL info = gd.GetOpenGLInfo();
                executionThreadAction = Task.Run(() => info.ExecuteOnGLThread(() =>
                {
                    actionEntered.Set();
                    Assert.True(SpinWait.SpinUntil(
                        () => gd.PendingBackendWorkItemCount != 0,
                        5000));

                    gd.WaitForIdle();
                    Assert.False(queuedDelegateEntered.IsSet);
                    firstBoundaryCompleted.Set();
                    Assert.True(submissionReturned.Wait(5000));

                    InvalidOperationException exception =
                        Assert.Throws<InvalidOperationException>(gd.WaitForIdle);
                    Assert.Contains(
                        "separates it from submitted native work",
                        exception.Message,
                        StringComparison.Ordinal);
                    Assert.False(queuedDelegateEntered.IsSet);
                }));

                Assert.True(actionEntered.Wait(5000));
                queuedTransaction = Task.Run(() => info.ExecuteOnGLThread(() =>
                {
                    queuedDelegateEntered.Set();
                    releaseQueuedDelegate.Wait();
                }));
                Assert.True(firstBoundaryCompleted.Wait(5000));

                gd.SubmitCommands(commandList, fence);
                submissionReturned.Set();

                Assert.True(executionThreadAction.Wait(5000));
                Assert.True(queuedDelegateEntered.Wait(5000));
                releaseQueuedDelegate.Set();
                Assert.True(queuedTransaction.Wait(5000));

                gd.WaitForIdle();
                Assert.True(fence.Signaled);
            }
            finally
            {
                submissionReturned.Set();
                releaseQueuedDelegate.Set();
                executionThreadAction?.Wait(5000);
                queuedTransaction?.Wait(5000);
                commandList?.Dispose();
                fence?.Dispose();
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task AdmittedConcurrentDisposeObservesOwnerTeardownFailure()
    {
        return Task.Run(() =>
        {
            Activator.CreateInstance<T>().CreateGraphicsDevice(
                out Sdl2Window window,
                out GraphicsDevice gd);
            InvalidOperationException sentinel = new InvalidOperationException(
                "OpenGL admitted-disposal teardown failure sentinel");
            Task executionThreadDispose = null;
            Task externalDispose = null;
            using ManualResetEventSlim actionEntered = new ManualResetEventSlim();
            using ManualResetEventSlim teardownPaused = new ManualResetEventSlim();
            using ManualResetEventSlim releaseTeardown = new ManualResetEventSlim();
            try
            {
                BackendInfoOpenGL info = gd.GetOpenGLInfo();
                gd.DisposeWhenIdle(new CallbackDisposable(() =>
                {
                    teardownPaused.Set();
                    releaseTeardown.Wait();
                    throw sentinel;
                }));
                executionThreadDispose = Task.Run(() => info.ExecuteOnGLThread(() =>
                {
                    actionEntered.Set();
                    Assert.True(SpinWait.SpinUntil(
                        () => gd.PendingBackendWorkItemCount != 0,
                        5000));
                    gd.Dispose();
                }));

                Assert.True(actionEntered.Wait(5000));
                externalDispose = Task.Run(gd.Dispose);
                Assert.True(teardownPaused.Wait(5000));
                releaseTeardown.Set();

                InvalidOperationException ownerFailure = Assert.Throws<InvalidOperationException>(
                    () => executionThreadDispose.GetAwaiter().GetResult());
                InvalidOperationException admittedFailure = Assert.Throws<InvalidOperationException>(
                    () => externalDispose.GetAwaiter().GetResult());
                Assert.Same(sentinel, ownerFailure);
                Assert.Same(sentinel, admittedFailure);
                Assert.True(gd.IsDisposed);
            }
            finally
            {
                releaseTeardown.Set();
                try
                {
                    executionThreadDispose?.Wait(5000);
                }
                catch (AggregateException)
                {
                }
                try
                {
                    externalDispose?.Wait(5000);
                }
                catch (AggregateException)
                {
                }
                gd.Dispose();
                window?.Close();
            }
        });
    }

    [Fact(Timeout = 10000)]
    public Task DebugInactiveWaitForIdleOwnsDeferredDrainOnExecutionThread()
    {
        return Task.Run(() =>
        {
            CreateNonDebugGraphicsDevice(out Sdl2Window window, out GraphicsDevice gd);
            try
            {
                Assert.False(gd.RequiresValidationBoundary);
                gd.DisposeWhenIdle(new CallbackDisposable(() =>
                    Assert.Throws<InvalidOperationException>(gd.Dispose)));

                gd.WaitForIdle();

                Assert.False(gd.IsDisposed);
            }
            finally
            {
                gd.Dispose();
                window?.Close();
            }
        });
    }

    private static void CreateNonDebugGraphicsDevice(
        out Sdl2Window window,
        out GraphicsDevice gd)
    {
        GraphicsBackend backend = typeof(T) == typeof(OpenGLDeviceCreator)
            ? GraphicsBackend.OpenGL
            : GraphicsBackend.OpenGLES;
        WindowCreateInfo windowCreateInfo = new WindowCreateInfo
        {
            WindowWidth = 200,
            WindowHeight = 200,
            WindowInitialState = WindowState.Hidden,
        };
        GraphicsDeviceOptions options = new GraphicsDeviceOptions(
            debug: false,
            swapchainDepthFormat: PixelFormat.R16_UNorm,
            syncToVerticalBlank: false);

        NeoVeldridStartup.CreateWindowAndGraphicsDevice(
            windowCreateInfo,
            options,
            backend,
            out window,
            out gd);
    }
}
#endif

#if TEST_OPENGL
[Trait("Backend", "OpenGL")]
public class OpenGLDeviceDisposalRegressionTests
    : OpenGLDeviceDisposalRegressionTests<OpenGLDeviceCreator>
{
}
#endif

#if TEST_OPENGLES
[Trait("Backend", "OpenGLES")]
public class OpenGLESDeviceDisposalRegressionTests
    : OpenGLDeviceDisposalRegressionTests<OpenGLESDeviceCreator>
{
}
#endif
