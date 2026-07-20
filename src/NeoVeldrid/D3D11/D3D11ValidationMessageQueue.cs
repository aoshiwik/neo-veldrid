using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using System;
using System.Runtime.InteropServices;

namespace NeoVeldrid.D3D11;

/// <summary>
/// Owns the Direct3D 11 debug interfaces and copies their process-external
/// messages into the device-owned validation history.
/// </summary>
internal unsafe sealed class D3D11ValidationMessageQueue : IDisposable
{
    private const ulong MessageCountLimit = 65_536;
    private static readonly nuint MaximumMessageByteLength = 16u * 1024u * 1024u;

    private readonly object _sync = new object();
    private ComPtr<ID3D11InfoQueue> _infoQueue;
    private ComPtr<ID3D11Debug> _debug;
    private ulong _nextMessageIndex;
    private ulong _lastDiscardedMessageCount;
    private ulong _lastStorageDeniedMessageCount;
    private ulong _collectedMessageCount;
    private bool _disposed;

    private D3D11ValidationMessageQueue(
        ID3D11InfoQueue* infoQueue,
        ID3D11Debug* debug,
        ulong creationBaselineDiscardedMessageCount,
        ulong creationBaselineStorageDeniedMessageCount)
    {
        _infoQueue = default;
        _infoQueue.Handle = infoQueue;
        _debug = default;
        _debug.Handle = debug;
        _lastDiscardedMessageCount = creationBaselineDiscardedMessageCount;
        _lastStorageDeniedMessageCount = creationBaselineStorageDeniedMessageCount;
        CreationBaselineDiscardedMessageCount = creationBaselineDiscardedMessageCount;
        CreationBaselineStorageDeniedMessageCount = creationBaselineStorageDeniedMessageCount;
    }

    internal ulong CreationBaselineDiscardedMessageCount { get; }

    internal ulong CreationBaselineStorageDeniedMessageCount { get; }

    internal bool SupportsLiveObjectTracking => _debug.Handle != null;

    internal ulong CollectedMessageCount
    {
        get
        {
            lock (_sync)
            {
                return _collectedMessageCount;
            }
        }
    }

    internal ulong DiscardedMessageCount
    {
        get
        {
            lock (_sync)
            {
                return _lastDiscardedMessageCount;
            }
        }
    }

    internal static bool TryCreate(
        ID3D11Device* device,
        out D3D11ValidationMessageQueue diagnostics,
        out string failureReason)
    {
        diagnostics = null;
        failureReason = string.Empty;

        if (device == null)
        {
            failureReason = "The Direct3D 11 device pointer was null.";
            return false;
        }

        ID3D11InfoQueue* infoQueue = null;
        ID3D11Debug* debug = null;
        try
        {
            Guid infoQueueGuid = ID3D11InfoQueue.Guid;
            int result = ((IUnknown*)device)->QueryInterface(&infoQueueGuid, (void**)&infoQueue);
            if (result < 0 || infoQueue == null)
            {
                failureReason =
                    $"ID3D11InfoQueue activation failed with HRESULT 0x{result:X8}.";
                return false;
            }

            // ID3D11InfoQueue cannot be queried until D3D11CreateDevice returns.
            // Capture that unavoidable creation baseline at the first possible
            // access, before any other NeoVeldrid device initialization work.
            ulong creationBaselineDiscarded =
                infoQueue->GetNumMessagesDiscardedByMessageCountLimit();
            ulong creationBaselineStorageDenied =
                infoQueue->GetNumMessagesDeniedByStorageFilter();

            result = infoQueue->SetMessageCountLimit(MessageCountLimit);
            if (result < 0)
            {
                failureReason =
                    $"ID3D11InfoQueue.SetMessageCountLimit failed with HRESULT 0x{result:X8}.";
                return false;
            }

            // NeoVeldrid owns this validation transport. Retrieval filters could
            // hide evidence, while storage filters can discard it permanently.
            infoQueue->ClearRetrievalFilter();
            infoQueue->ClearStorageFilter();

            // Acquire live-object diagnostics only after removing the storage
            // filter, so any messages caused by subsequent activation work are
            // retained and every later denied-counter increase is fatal.
            Guid debugGuid = ID3D11Debug.Guid;
            result = ((IUnknown*)device)->QueryInterface(&debugGuid, (void**)&debug);
            if (result < 0 || debug == null)
            {
                failureReason =
                    $"ID3D11Debug activation failed with HRESULT 0x{result:X8}.";
                return false;
            }

            diagnostics = new D3D11ValidationMessageQueue(
                infoQueue,
                debug,
                creationBaselineDiscarded,
                creationBaselineStorageDenied);
            infoQueue = null;
            debug = null;
            return true;
        }
        catch (Exception exception)
        {
            failureReason = $"Direct3D 11 debug-interface activation failed: {exception.Message}";
            return false;
        }
        finally
        {
            if (debug != null)
            {
                debug->Release();
            }
            if (infoQueue != null)
            {
                infoQueue->Release();
            }
        }
    }

    internal void DrainTo(GraphicsDeviceValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            DrainLocked(validation, liveObjectReport: false);
        }
    }

    internal void ReportLiveObjectsAndDrainTo(GraphicsDeviceValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                int result = _debug.Handle->ReportLiveDeviceObjects(
                    RldoFlags.Summary | RldoFlags.Detail | RldoFlags.IgnoreInternal);
                if (result < 0)
                {
                    validation.Report(
                        GraphicsDeviceValidationSeverity.Error,
                        "D3D11",
                        "LiveObjectTracking",
                        "ReportLiveDeviceObjectsFailed",
                        $"ID3D11Debug.ReportLiveDeviceObjects failed with HRESULT 0x{result:X8}.");
                }
            }
            catch (Exception exception)
            {
                validation.Report(
                    GraphicsDeviceValidationSeverity.Error,
                    "D3D11",
                    "LiveObjectTracking",
                    "ReportLiveDeviceObjectsFailed",
                    exception.ToString());
            }

            DrainLocked(validation, liveObjectReport: true);
        }
    }

    private void DrainLocked(GraphicsDeviceValidation validation, bool liveObjectReport)
    {
        try
        {
            ReportLostMessagesLocked(validation);

            ulong storedMessageCount = _infoQueue.Handle->GetNumStoredMessages();
            ulong retrievableMessageCount =
                _infoQueue.Handle->GetNumStoredMessagesAllowedByRetrievalFilter();
            if (retrievableMessageCount != storedMessageCount)
            {
                validation.Report(
                    GraphicsDeviceValidationSeverity.Warning,
                    "D3D11",
                    "InfoQueue",
                    "RetrievalFilterRemoved",
                    $"The retrieval filter exposed {retrievableMessageCount} of "
                    + $"{storedMessageCount} stored messages. NeoVeldrid removed it before draining.");
                _infoQueue.Handle->ClearRetrievalFilter();
                storedMessageCount = _infoQueue.Handle->GetNumStoredMessages();
            }

            if (storedMessageCount < _nextMessageIndex)
            {
                validation.Report(
                    GraphicsDeviceValidationSeverity.Error,
                    "D3D11",
                    "InfoQueue",
                    "QueueClearedExternally",
                    $"The native message count fell from {_nextMessageIndex} to {storedMessageCount}; "
                    + "validation evidence was cleared outside NeoVeldrid.");
                _nextMessageIndex = 0;
            }

            ulong drainEnd = storedMessageCount;
            for (ulong index = _nextMessageIndex; index < drainEnd; index++)
            {
                try
                {
                    CopyMessageLocked(validation, index, liveObjectReport);
                }
                catch (Exception exception)
                {
                    // A malformed or transiently unreadable native message must
                    // neither replay earlier evidence nor prevent later messages
                    // from being captured. Record the missing evidence as fatal
                    // and commit this native slot exactly once.
                    validation.Report(
                        GraphicsDeviceValidationSeverity.Error,
                        "D3D11",
                        liveObjectReport ? "LiveObjectTracking" : "InfoQueue",
                        "MessageCopyFailed",
                        $"Could not copy native validation message {index}: {exception}");
                }
                finally
                {
                    _nextMessageIndex = index + 1;
                }
            }

            ReportLostMessagesLocked(validation);
        }
        catch (Exception exception)
        {
            validation.Report(
                GraphicsDeviceValidationSeverity.Error,
                "D3D11",
                liveObjectReport ? "LiveObjectTracking" : "InfoQueue",
                "MessageDrainFailed",
                exception.ToString());
        }
    }

    private void CopyMessageLocked(
        GraphicsDeviceValidation validation,
        ulong index,
        bool liveObjectReport)
    {
        nuint messageByteLength = 0;
        SilkMarshal.ThrowHResult(
            _infoQueue.Handle->GetMessageA(index, (Message*)null, &messageByteLength));

        if (messageByteLength < (nuint)sizeof(Message)
            || messageByteLength > MaximumMessageByteLength)
        {
            throw new InvalidOperationException(
                $"ID3D11InfoQueue returned an invalid message size of {messageByteLength} bytes.");
        }

        void* allocation = NativeMemory.Alloc(messageByteLength);
        if (allocation == null)
        {
            throw new OutOfMemoryException(
                $"Could not allocate {messageByteLength} bytes for a Direct3D 11 validation message.");
        }

        try
        {
            Message* message = (Message*)allocation;
            nuint actualByteLength = messageByteLength;
            SilkMarshal.ThrowHResult(
                _infoQueue.Handle->GetMessageA(index, message, &actualByteLength));

            string text = CopyDescription(message);
            GraphicsDeviceValidationSeverity severity = NormalizeSeverity(message->Severity);
            string type = message->Category.ToString();
            if (liveObjectReport)
            {
                type = "LiveObject";
                if (!IsApprovedLiveObjectEvidence(message->ID))
                {
                    severity = GraphicsDeviceValidationSeverity.Error;
                }
            }

            validation.Report(
                severity,
                "D3D11",
                type,
                message->ID.ToString(),
                text);
            _collectedMessageCount++;
        }
        finally
        {
            NativeMemory.Free(allocation);
        }
    }

    private void ReportLostMessagesLocked(GraphicsDeviceValidation validation)
    {
        ulong discarded = _infoQueue.Handle->GetNumMessagesDiscardedByMessageCountLimit();
        if (discarded < _lastDiscardedMessageCount)
        {
            validation.Report(
                GraphicsDeviceValidationSeverity.Error,
                "D3D11",
                "InfoQueue",
                "DiscardCounterReset",
                "The native discarded-message counter moved backwards; validation evidence is incomplete.");
        }
        else if (discarded > _lastDiscardedMessageCount)
        {
            ulong difference = discarded - _lastDiscardedMessageCount;
            validation.Report(
                GraphicsDeviceValidationSeverity.Error,
                "D3D11",
                "InfoQueue",
                "MessagesDiscarded",
                $"The native queue discarded {difference} validation message(s) because its "
                + $"{MessageCountLimit}-message capacity was exceeded.");
        }
        _lastDiscardedMessageCount = discarded;

        ulong denied = _infoQueue.Handle->GetNumMessagesDeniedByStorageFilter();
        if (denied < _lastStorageDeniedMessageCount)
        {
            validation.Report(
                GraphicsDeviceValidationSeverity.Error,
                "D3D11",
                "InfoQueue",
                "StorageFilterCounterReset",
                "The native storage-filter counter moved backwards; validation evidence is incomplete.");
        }
        else if (denied > _lastStorageDeniedMessageCount)
        {
            ulong difference = denied - _lastStorageDeniedMessageCount;
            validation.Report(
                GraphicsDeviceValidationSeverity.Error,
                "D3D11",
                "InfoQueue",
                "MessagesDeniedByStorageFilter",
                $"A storage filter permanently denied {difference} validation message(s).");
        }
        _lastStorageDeniedMessageCount = denied;
    }

    private static string CopyDescription(Message* message)
    {
        if (message->PDescription == null || message->DescriptionByteLength == 0)
        {
            return string.Empty;
        }

        nuint byteLength = message->DescriptionByteLength;
        if (message->PDescription[byteLength - 1] == 0)
        {
            byteLength--;
        }

        if (byteLength > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"The Direct3D 11 validation description is too large ({byteLength} bytes).");
        }

        return Marshal.PtrToStringAnsi((nint)message->PDescription, (int)byteLength)
            ?? string.Empty;
    }

    private static GraphicsDeviceValidationSeverity NormalizeSeverity(MessageSeverity severity) =>
        severity switch
        {
            MessageSeverity.Corruption => GraphicsDeviceValidationSeverity.Corruption,
            MessageSeverity.Error => GraphicsDeviceValidationSeverity.Error,
            MessageSeverity.Warning => GraphicsDeviceValidationSeverity.Warning,
            MessageSeverity.Info => GraphicsDeviceValidationSeverity.Information,
            MessageSeverity.Message => GraphicsDeviceValidationSeverity.Verbose,
            _ => GraphicsDeviceValidationSeverity.Warning,
        };

    private static bool IsApprovedLiveObjectEvidence(MessageID id) =>
        id == MessageID.LiveDevice
        || id == MessageID.LiveObjectSummary
        || id == MessageID.LiveDeviceWin7
        || id == MessageID.LiveObjectSummaryWin7;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debug.Dispose();
            _debug = default;
            _infoQueue.Dispose();
            _infoQueue = default;
        }
    }
}
