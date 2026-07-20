using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace NeoVeldrid;

/// <summary>
/// Identifies validation capabilities which a graphics device has proved are active.
/// </summary>
[Flags]
public enum GraphicsDeviceValidationFeatures
{
    None = 0,
    ApiDebugOutput = 1 << 0,
    SynchronousMessageDelivery = 1 << 1,
    SynchronizationValidation = 1 << 2,
    LiveObjectTracking = 1 << 3,
}

/// <summary>
/// Normalized severity for a native graphics validation message.
/// </summary>
public enum GraphicsDeviceValidationSeverity
{
    Verbose,
    Information,
    Warning,
    Error,
    Corruption,
}

/// <summary>
/// An immutable, backend-neutral native graphics validation message.
/// </summary>
public readonly record struct GraphicsDeviceValidationMessage(
    long Sequence,
    GraphicsBackend Backend,
    GraphicsDeviceValidationSeverity Severity,
    string Source,
    string Type,
    string Id,
    string Text)
{
    /// <summary>
    /// Gets whether this message fails a validation checkpoint.
    /// </summary>
    public bool IsError =>
        Severity == GraphicsDeviceValidationSeverity.Error
        || Severity == GraphicsDeviceValidationSeverity.Corruption;

    public override string ToString() =>
        $"#{Sequence} [{Backend}/{Severity}] {Source}/{Type}/{Id}: {Text}";
}

/// <summary>
/// Describes the debug and validation facilities proved active for a graphics device.
/// </summary>
public readonly record struct GraphicsDeviceValidationStatus(
    bool Requested,
    GraphicsDeviceValidationFeatures ActiveFeatures,
    string Transport,
    string InactiveReason)
{
    /// <summary>
    /// Gets whether an API debug-message transport has been activated and verified.
    /// </summary>
    public bool IsActive =>
        (ActiveFeatures & GraphicsDeviceValidationFeatures.ApiDebugOutput) != 0;

    /// <summary>
    /// Gets whether the given validation feature has been proved active.
    /// </summary>
    public bool HasFeature(GraphicsDeviceValidationFeatures feature) =>
        (ActiveFeatures & feature) == feature;

    public override string ToString()
    {
        if (IsActive)
        {
            return $"Active ({Transport}; {ActiveFeatures})";
        }

        string reason = string.IsNullOrWhiteSpace(InactiveReason)
            ? "no activation evidence"
            : InactiveReason;
        return $"Inactive (requested: {Requested}; {reason})";
    }
}

/// <summary>
/// Thrown when one or more native graphics validation errors are observed at a lifecycle checkpoint.
/// </summary>
public sealed class GraphicsDeviceValidationException : NeoVeldridException
{
    internal GraphicsDeviceValidationException(
        GraphicsBackend backend,
        string boundary,
        IReadOnlyList<GraphicsDeviceValidationMessage> messages)
        : base(CreateMessage(backend, boundary, messages))
    {
        Backend = backend;
        Boundary = boundary;
        Messages = messages;
    }

    /// <summary>
    /// Gets the backend which produced the validation errors.
    /// </summary>
    public GraphicsBackend Backend { get; }

    /// <summary>
    /// Gets the lifecycle boundary which observed the validation errors.
    /// </summary>
    public string Boundary { get; }

    /// <summary>
    /// Gets every error observed at this boundary, in callback order.
    /// </summary>
    public IReadOnlyList<GraphicsDeviceValidationMessage> Messages { get; }

    private static string CreateMessage(
        GraphicsBackend backend,
        string boundary,
        IReadOnlyList<GraphicsDeviceValidationMessage> messages)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append(backend)
            .Append(" validation failed at ")
            .Append(boundary)
            .Append(" with ")
            .Append(messages.Count)
            .Append(messages.Count == 1 ? " error:" : " errors:");

        foreach (GraphicsDeviceValidationMessage message in messages)
        {
            builder.AppendLine().Append(message);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Owns validation activation evidence and the ordered native-message history for one graphics device.
/// </summary>
/// <remarks>
/// Native callbacks may execute on driver threads. All mutation is serialized, messages are never stored
/// process-wide, and a checkpoint advances atomically so a message cannot fail two unrelated operations.
/// The complete history remains available for diagnostics after checkpoints and device disposal.
/// </remarks>
public sealed class GraphicsDeviceValidation
{
    private readonly object _sync = new object();
    private readonly object _boundarySync = new object();
    private readonly GraphicsBackend _backend;
    private readonly List<GraphicsDeviceValidationMessage> _messages =
        new List<GraphicsDeviceValidationMessage>();
    private GraphicsDeviceValidationStatus _status;
    private long _nextSequence;
    private int _checkpointIndex;
    private int _boundaryChecksRequired;
    private bool _sealed;

    internal GraphicsDeviceValidation(GraphicsBackend backend, bool requested)
    {
        _backend = backend;
        _status = new GraphicsDeviceValidationStatus(
            requested,
            GraphicsDeviceValidationFeatures.None,
            string.Empty,
            requested ? "backend validation has not been activated" : "debug validation was not requested");
    }

    /// <summary>
    /// Gets the latest immutable activation status.
    /// </summary>
    public GraphicsDeviceValidationStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Gets a stable copy of every message observed during this device lifetime.
    /// </summary>
    public IReadOnlyList<GraphicsDeviceValidationMessage> GetMessageHistory()
    {
        lock (_sync)
        {
            return new ReadOnlyCollection<GraphicsDeviceValidationMessage>(_messages.ToArray());
        }
    }

    internal long NextSequence
    {
        get
        {
            lock (_sync)
            {
                return _nextSequence + 1;
            }
        }
    }

    /// <summary>
    /// Gets whether lifecycle operations need to enter the validation boundary.
    /// Inactive non-debug devices use this as a lock-free fast path.
    /// </summary>
    internal bool RequiresBoundaryChecks =>
        Volatile.Read(ref _boundaryChecksRequired) != 0;

    internal void SetActive(
        GraphicsDeviceValidationFeatures features,
        string transport)
    {
        if ((features & GraphicsDeviceValidationFeatures.ApiDebugOutput) == 0)
        {
            throw new ArgumentException(
                $"{nameof(GraphicsDeviceValidationFeatures.ApiDebugOutput)} is required for active validation.",
                nameof(features));
        }

        lock (_sync)
        {
            ThrowIfSealed();
            _status = new GraphicsDeviceValidationStatus(
                _status.Requested,
                features,
                transport ?? string.Empty,
                string.Empty);
            Volatile.Write(ref _boundaryChecksRequired, 1);
        }
    }

    internal void SetInactive(string reason)
    {
        lock (_sync)
        {
            ThrowIfSealed();
            _status = new GraphicsDeviceValidationStatus(
                _status.Requested,
                GraphicsDeviceValidationFeatures.None,
                string.Empty,
                reason ?? string.Empty);
            Volatile.Write(
                ref _boundaryChecksRequired,
                _checkpointIndex < _messages.Count ? 1 : 0);
        }
    }

    internal GraphicsDeviceValidationMessage Report(
        GraphicsDeviceValidationSeverity severity,
        string source,
        string type,
        string id,
        string text)
    {
        lock (_sync)
        {
            ThrowIfSealed();
            GraphicsDeviceValidationMessage message = new GraphicsDeviceValidationMessage(
                ++_nextSequence,
                _backend,
                severity,
                source ?? string.Empty,
                type ?? string.Empty,
                id ?? string.Empty,
                text ?? string.Empty);
            _messages.Add(message);
            Volatile.Write(ref _boundaryChecksRequired, 1);
            return message;
        }
    }

    internal bool HasMessageSince(long firstSequence, Func<GraphicsDeviceValidationMessage, bool> predicate)
    {
        lock (_sync)
        {
            return _messages.Any(message => message.Sequence >= firstSequence && predicate(message));
        }
    }

    /// <summary>
    /// Runs an operation and owns all validation evidence through the following
    /// collection and checkpoint as one serialized lifecycle boundary.
    /// </summary>
    internal void ExecuteBoundary(string boundary, Action operation, Action collect) =>
        ExecuteBoundary(boundary, operation, static action => action(), collect);

    /// <summary>
    /// Runs a stateful operation without requiring callers to allocate a capturing
    /// closure for each validated lifecycle boundary.
    /// </summary>
    internal void ExecuteBoundary<TState>(
        string boundary,
        TState state,
        Action<TState> operation,
        Action collect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        ArgumentNullException.ThrowIfNull(operation);

        if (!RequiresBoundaryChecks)
        {
            operation(state);
            return;
        }

        lock (_boundarySync)
        {
            List<Exception> failures = null;
            try
            {
                operation(state);
            }
            catch (Exception exception)
            {
                AddFailure(exception, ref failures);
            }
            Attempt(collect, ref failures);
            try
            {
                ConsumeCheckpoint(boundary, seal: false);
            }
            catch (Exception exception)
            {
                AddFailure(exception, ref failures);
            }
            ThrowFailures(boundary, failures);
        }
    }

    internal IReadOnlyList<GraphicsDeviceValidationMessage> Checkpoint(string boundary) =>
        Checkpoint(boundary, collect: null);

    /// <summary>
    /// Atomically collects a polling backend and advances the shared checkpoint.
    /// </summary>
    internal IReadOnlyList<GraphicsDeviceValidationMessage> Checkpoint(
        string boundary,
        Action collect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);

        if (!RequiresBoundaryChecks)
        {
            return Array.Empty<GraphicsDeviceValidationMessage>();
        }

        lock (_boundarySync)
        {
            List<Exception> failures = null;
            Attempt(collect, ref failures);

            IReadOnlyList<GraphicsDeviceValidationMessage> observed =
                Array.Empty<GraphicsDeviceValidationMessage>();
            try
            {
                observed = ConsumeCheckpoint(boundary, seal: false);
            }
            catch (Exception exception)
            {
                AddFailure(exception, ref failures);
            }
            ThrowFailures(boundary, failures);
            return observed;
        }
    }

    /// <summary>
    /// Performs the final polling collection, consumes its evidence, and seals
    /// the collector so disposed devices no longer claim active validation.
    /// </summary>
    internal void Seal(string boundary, Action collect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);

        lock (_boundarySync)
        {
            lock (_sync)
            {
                if (_sealed)
                {
                    return;
                }
            }

            List<Exception> failures = null;
            Attempt(collect, ref failures);
            try
            {
                ConsumeCheckpoint(boundary, seal: true);
            }
            catch (Exception exception)
            {
                AddFailure(exception, ref failures);
            }
            ThrowFailures(boundary, failures);
        }
    }

    private IReadOnlyList<GraphicsDeviceValidationMessage> ConsumeCheckpoint(
        string boundary,
        bool seal)
    {
        GraphicsDeviceValidationMessage[] observed;
        lock (_sync)
        {
            if (_sealed)
            {
                return Array.Empty<GraphicsDeviceValidationMessage>();
            }

            int count = _messages.Count - _checkpointIndex;
            observed = count == 0
                ? Array.Empty<GraphicsDeviceValidationMessage>()
                : _messages.GetRange(_checkpointIndex, count).ToArray();
            _checkpointIndex = _messages.Count;

            if (seal)
            {
                _sealed = true;
                _status = new GraphicsDeviceValidationStatus(
                    _status.Requested,
                    GraphicsDeviceValidationFeatures.None,
                    string.Empty,
                    "the graphics device has been disposed");
                Volatile.Write(ref _boundaryChecksRequired, 0);
            }
            else if (!_status.IsActive && _checkpointIndex == _messages.Count)
            {
                Volatile.Write(ref _boundaryChecksRequired, 0);
            }
        }

        List<GraphicsDeviceValidationMessage> errors = null;
        foreach (GraphicsDeviceValidationMessage message in observed)
        {
            if (message.IsError)
            {
                errors ??= new List<GraphicsDeviceValidationMessage>();
                errors.Add(message);
            }
        }
        if (errors != null)
        {
            throw new GraphicsDeviceValidationException(_backend, boundary, errors.ToArray());
        }

        return observed;
    }

    private void ThrowIfSealed()
    {
        if (_sealed)
        {
            throw new ObjectDisposedException(
                nameof(GraphicsDeviceValidation),
                "Validation diagnostics have been sealed because the graphics device was disposed.");
        }
    }

    private static void Attempt(Action action, ref List<Exception> failures)
    {
        if (action == null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception exception)
        {
            AddFailure(exception, ref failures);
        }
    }

    private static void AddFailure(Exception exception, ref List<Exception> failures)
    {
        failures ??= new List<Exception>();
        failures.Add(exception);
    }

    private static void ThrowFailures(string boundary, List<Exception> failures)
    {
        if (failures == null || failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(
            $"Validation boundary '{boundary}' encountered multiple failures.",
            failures);
    }
}
