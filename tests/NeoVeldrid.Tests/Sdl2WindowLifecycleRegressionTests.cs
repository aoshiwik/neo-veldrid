using System;
using NeoVeldrid.Sdl2;
using Xunit;

namespace NeoVeldrid.Tests;

public sealed class Sdl2WindowLifecycleRegressionTests
{
    [Fact]
    public void Close_VetoReleasesCloseGateForLaterRetry()
    {
        var harness = new CloseHarness
        {
            CloseRequestedHandler = () => true,
        };

        Assert.Equal(Sdl2WindowCloseResult.Vetoed, harness.Close());
        Assert.Equal(0, harness.UnregisterCount);
        Assert.Equal(0, harness.DestroyCount);

        harness.CloseRequestedHandler = null;

        Assert.Equal(Sdl2WindowCloseResult.Closed, harness.Close());
        Assert.Equal(1, harness.UnregisterCount);
        Assert.Equal(1, harness.DestroyCount);
    }

    [Fact]
    public void Close_ReentrantObserverDoesNotDestroyTwiceOrRecurse()
    {
        var harness = new CloseHarness();
        Sdl2WindowCloseResult reentrantResult = Sdl2WindowCloseResult.Closed;
        var closingCount = 0;
        var closedCount = 0;
        harness.Closing += () =>
        {
            closingCount++;
            reentrantResult = harness.Close();
        };
        harness.Closed += () => closedCount++;

        Assert.Equal(Sdl2WindowCloseResult.Closed, harness.Close());
        Assert.Equal(Sdl2WindowCloseResult.AlreadyClosing, reentrantResult);
        Assert.Equal(Sdl2WindowCloseResult.AlreadyClosing, harness.Close());
        Assert.Equal(1, harness.UnregisterCount);
        Assert.Equal(1, harness.DestroyCount);
        Assert.Equal(1, closingCount);
        Assert.Equal(1, closedCount);
    }

    [Fact]
    public void Close_StillClosesWhenCloseRequestedHandlerThrows()
    {
        var harness = new CloseHarness();
        var policyFailure = new InvalidOperationException("close policy");
        var closingCount = 0;
        var closedCount = 0;
        harness.CloseRequestedHandler = () => throw policyFailure;
        harness.Closing += () => closingCount++;
        harness.Closed += () => closedCount++;

        var thrown = Assert.Throws<InvalidOperationException>(() => harness.Close());

        Assert.Same(policyFailure, thrown);
        Assert.Equal(1, harness.UnregisterCount);
        Assert.Equal(1, harness.DestroyCount);
        Assert.Equal(1, closingCount);
        Assert.Equal(1, closedCount);
        Assert.Equal(Sdl2WindowCloseResult.AlreadyClosing, harness.Close());
    }

    [Fact]
    public void Close_StillDestroysWindowWhenClosingObserverThrows()
    {
        var harness = new CloseHarness();
        var closingFailure = new InvalidOperationException("closing observer");
        var laterClosingCount = 0;
        var closedCount = 0;
        harness.Closing += () => throw closingFailure;
        harness.Closing += () => laterClosingCount++;
        harness.Closed += () => closedCount++;

        var thrown = Assert.Throws<InvalidOperationException>(() => harness.Close());

        Assert.Same(closingFailure, thrown);
        Assert.Equal(1, harness.UnregisterCount);
        Assert.Equal(1, harness.DestroyCount);
        Assert.Equal(1, laterClosingCount);
        Assert.Equal(1, closedCount);
        Assert.Equal(Sdl2WindowCloseResult.AlreadyClosing, harness.Close());
    }

    [Fact]
    public void Close_AggregatesFailuresAfterAllObserversRun()
    {
        var harness = new CloseHarness();
        var closingFailure = new InvalidOperationException("closing observer");
        var destroyFailure = new InvalidOperationException("destroy window");
        var closedFailure = new InvalidOperationException("closed observer");
        var laterClosingCount = 0;
        var laterClosedCount = 0;
        harness.Closing += () => throw closingFailure;
        harness.Closing += () => laterClosingCount++;
        harness.DestroyFailure = destroyFailure;
        harness.Closed += () => throw closedFailure;
        harness.Closed += () => laterClosedCount++;

        AggregateException thrown = Assert.Throws<AggregateException>(() => harness.Close());

        Assert.Equal(
            new Exception[] { closingFailure, destroyFailure, closedFailure },
            thrown.InnerExceptions);
        Assert.Equal(1, harness.UnregisterCount);
        Assert.Equal(1, harness.DestroyCount);
        Assert.Equal(1, laterClosingCount);
        Assert.Equal(1, laterClosedCount);
        Assert.Equal(Sdl2WindowCloseResult.AlreadyClosing, harness.Close());
    }

    private sealed class CloseHarness
    {
        private readonly Sdl2WindowCloseCoordinator _coordinator = new();

        public Func<bool> CloseRequestedHandler { get; set; }
        public event Action Closing;
        public event Action Closed;
        public Exception DestroyFailure { get; set; }
        public int UnregisterCount { get; private set; }
        public int DestroyCount { get; private set; }

        public Sdl2WindowCloseResult Close() => _coordinator.TryClose(
            CloseRequestedHandler,
            () => UnregisterCount++,
            () => Closing,
            Destroy,
            () => Closed);

        private void Destroy()
        {
            DestroyCount++;
            if (DestroyFailure != null)
            {
                throw DestroyFailure;
            }
        }
    }
}
