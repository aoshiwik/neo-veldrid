using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace NeoVeldrid.Tests;

public sealed class GraphicsDeviceValidationTests
{
    [Fact]
    public void MessagesAreRetainedInOrderAndCheckpointedExactlyOnce()
    {
        GraphicsDeviceValidation validation = CreateValidation();
        validation.Report(
            GraphicsDeviceValidationSeverity.Warning,
            "driver",
            "performance",
            "first",
            "First message");
        validation.Report(
            GraphicsDeviceValidationSeverity.Information,
            "application",
            "marker",
            "second",
            "Second message");

        GraphicsDeviceValidationMessage[] firstCheckpoint = validation.Checkpoint("first").ToArray();

        Assert.Equal(new long[] { 1, 2 }, firstCheckpoint.Select(message => message.Sequence));
        Assert.Empty(validation.Checkpoint("second"));
        Assert.Equal(2, validation.GetMessageHistory().Count);
    }

    [Fact]
    public void MultipleErrorsAreAggregatedWithoutOverwriting()
    {
        GraphicsDeviceValidation validation = CreateValidation();
        validation.Report(GraphicsDeviceValidationSeverity.Error, "api", "validation", "one", "First error");
        validation.Report(GraphicsDeviceValidationSeverity.Corruption, "api", "validation", "two", "Second error");

        GraphicsDeviceValidationException exception = Assert.Throws<GraphicsDeviceValidationException>(
            () => validation.Checkpoint("submission"));

        Assert.Equal(2, exception.Messages.Count);
        Assert.Contains("First error", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Second error", exception.Message, StringComparison.Ordinal);
        Assert.Empty(validation.Checkpoint("after-failure"));
    }

    [Fact]
    public async Task ConcurrentProducersLoseNoMessages()
    {
        GraphicsDeviceValidation validation = CreateValidation();
        const int producerCount = 8;
        const int messagesPerProducer = 100;

        await Task.WhenAll(Enumerable.Range(0, producerCount).Select(producer => Task.Run(() =>
        {
            for (int index = 0; index < messagesPerProducer; index++)
            {
                validation.Report(
                    GraphicsDeviceValidationSeverity.Information,
                    "test",
                    "concurrency",
                    producer.ToString(),
                    index.ToString());
            }
        })));

        GraphicsDeviceValidationMessage[] history = validation.GetMessageHistory().ToArray();
        Assert.Equal(producerCount * messagesPerProducer, history.Length);
        Assert.Equal(
            Enumerable.Range(1, history.Length).Select(value => (long)value),
            history.Select(message => message.Sequence));
    }

    [Fact]
    public void OperationAndValidationFailuresArePreservedAtTheirBoundary()
    {
        GraphicsDeviceValidation validation = CreateActiveValidation();
        InvalidOperationException operationFailure = new InvalidOperationException("native operation failed");

        AggregateException exception = Assert.Throws<AggregateException>(() =>
            validation.ExecuteBoundary(
                "submission",
                () =>
                {
                    validation.Report(
                        GraphicsDeviceValidationSeverity.Error,
                        "test",
                        "boundary",
                        "validation-error",
                        "native validation failed");
                    throw operationFailure;
                },
                collect: null));

        Assert.Same(operationFailure, exception.InnerExceptions[0]);
        GraphicsDeviceValidationException validationFailure =
            Assert.IsType<GraphicsDeviceValidationException>(exception.InnerExceptions[1]);
        Assert.Equal("validation-error", Assert.Single(validationFailure.Messages).Id);
        Assert.Empty(validation.Checkpoint("after aggregate"));
    }

    [Fact]
    public async Task ConcurrentBoundariesCannotConsumeEachOthersErrors()
    {
        GraphicsDeviceValidation validation = CreateActiveValidation();
        const int boundaryCount = 64;

        string[] observedIds = await Task.WhenAll(
            Enumerable.Range(0, boundaryCount).Select(index => Task.Run(() =>
            {
                string id = $"boundary-{index}";
                GraphicsDeviceValidationException exception =
                    Assert.Throws<GraphicsDeviceValidationException>(() =>
                        validation.ExecuteBoundary(
                            id,
                            () => validation.Report(
                                GraphicsDeviceValidationSeverity.Error,
                                "test",
                                "concurrent-boundary",
                                id,
                                id),
                            collect: null));
                return Assert.Single(exception.Messages).Id;
            })));

        Assert.Equal(
            Enumerable.Range(0, boundaryCount).Select(index => $"boundary-{index}").OrderBy(id => id),
            observedIds.OrderBy(id => id));
    }

    [Fact]
    public void CollectorAndValidationFailuresAreBothPreserved()
    {
        GraphicsDeviceValidation validation = CreateActiveValidation();
        InvalidOperationException collectionFailure = new InvalidOperationException("collection failed");

        AggregateException exception = Assert.Throws<AggregateException>(() =>
            validation.Checkpoint(
                "polling",
                () =>
                {
                    validation.Report(
                        GraphicsDeviceValidationSeverity.Error,
                        "test",
                        "polling",
                        "collected-before-failure",
                        "collected before the poll failed");
                    throw collectionFailure;
                }));

        Assert.Same(collectionFailure, exception.InnerExceptions[0]);
        Assert.IsType<GraphicsDeviceValidationException>(exception.InnerExceptions[1]);
        Assert.Empty(validation.Checkpoint("after polling failure"));
    }

    [Fact]
    public void SealingClearsActivationAndRejectsLateMessages()
    {
        GraphicsDeviceValidation validation = CreateActiveValidation();

        validation.Seal("teardown", collect: null);

        Assert.False(validation.Status.IsActive);
        Assert.Contains("disposed", validation.Status.InactiveReason, StringComparison.Ordinal);
        Assert.False(validation.RequiresBoundaryChecks);
        Assert.Throws<ObjectDisposedException>(() => validation.Report(
            GraphicsDeviceValidationSeverity.Error,
            "test",
            "late-callback",
            "late",
            "late"));
    }

    [Fact]
    public void InactiveDeviceUsesBoundaryFastPath()
    {
        GraphicsDeviceValidation validation =
            new GraphicsDeviceValidation(GraphicsBackend.OpenGL, requested: false);
        int operations = 0;
        int collections = 0;

        validation.ExecuteBoundary(
            "inactive",
            () => operations++,
            () => collections++);

        Assert.Equal(1, operations);
        Assert.Equal(0, collections);
        Assert.False(validation.RequiresBoundaryChecks);
    }

    [Fact]
    public void CleanActiveBoundariesDoNotAllocateAfterWarmup()
    {
        GraphicsDeviceValidation validation = CreateActiveValidation();
        Action<int> cleanOperation = static _ => { };
        for (int index = 0; index < 16; index++)
        {
            validation.ExecuteBoundary("clean", index, cleanOperation, collect: null);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
        {
            validation.ExecuteBoundary("clean", index, cleanOperation, collect: null);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DeviceCollectorsAreIsolated()
    {
        GraphicsDeviceValidation first = CreateValidation();
        GraphicsDeviceValidation second = CreateValidation();
        first.Report(GraphicsDeviceValidationSeverity.Warning, "test", "isolation", "one", "First only");

        Assert.Single(first.GetMessageHistory());
        Assert.Empty(second.GetMessageHistory());
    }

    private static GraphicsDeviceValidation CreateValidation() =>
        new GraphicsDeviceValidation(GraphicsBackend.Vulkan, requested: true);

    private static GraphicsDeviceValidation CreateActiveValidation()
    {
        GraphicsDeviceValidation validation = CreateValidation();
        validation.SetActive(
            GraphicsDeviceValidationFeatures.ApiDebugOutput,
            "synthetic transport");
        return validation;
    }
}
