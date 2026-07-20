using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace NeoVeldrid.OpenGL;

internal class OpenGLFence : Fence
{
    private readonly ManualResetEvent _mre;
    private readonly object _sync = new object();
    private bool _submissionPending;
    private Exception _submissionException;
    private int _activeWaiters;
    private bool _disposeRequested;
    private bool _eventDisposed;

    public OpenGLFence(bool signaled)
    {
        _mre = new ManualResetEvent(signaled);
    }

    public override string Name { get; set; }

    public override void Reset()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_submissionPending)
            {
                throw new InvalidOperationException(
                    "An OpenGL fence cannot be reset while its submission is still pending.");
            }
            if (_activeWaiters != 0)
            {
                throw new InvalidOperationException(
                    "An OpenGL fence cannot be reset while another thread is waiting on it.");
            }

            _submissionException = null;
            _mre.Reset();
        }
    }

    public override bool Signaled
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return _mre.WaitOne(0);
            }
        }
    }

    public override bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposeRequested;
            }
        }
    }

    public override void Dispose()
    {
        lock (_sync)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            TryDisposeEvent();
        }
    }

    internal void BeginSubmission()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_submissionPending)
            {
                throw new InvalidOperationException(
                    "An OpenGL fence cannot be associated with multiple in-flight submissions.");
            }
            if (_mre.WaitOne(0))
            {
                throw new InvalidOperationException(
                    "An OpenGL fence must be reset before it is submitted again.");
            }

            _submissionException = null;
            _submissionPending = true;
        }
    }

    internal void CancelSubmission()
    {
        lock (_sync)
        {
            if (!_submissionPending)
            {
                return;
            }

            _submissionPending = false;
            TryDisposeEvent();
        }
    }

    internal void CompleteSubmission(Exception exception)
    {
        lock (_sync)
        {
            if (!_submissionPending)
            {
                throw new InvalidOperationException(
                    "The OpenGL fence completed without a matching pending submission.");
            }

            _submissionException = exception;
            _mre.Set();
            _submissionPending = false;
            TryDisposeEvent();
        }
    }

    internal bool Wait(ulong nanosecondTimeout)
    {
        ManualResetEvent waitHandle = AcquireWaitHandle();
        int timeout = nanosecondTimeout == ulong.MaxValue
            ? Timeout.Infinite
            : (int)Math.Min(int.MaxValue, nanosecondTimeout / 1_000_000);
        try
        {
            bool signaled = waitHandle.WaitOne(timeout);
            if (signaled)
            {
                ThrowSubmissionException();
            }
            return signaled;
        }
        finally
        {
            ReleaseWaitHandle();
        }
    }

    internal ManualResetEvent AcquireWaitHandle()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _activeWaiters++;
            return _mre;
        }
    }

    internal void ReleaseWaitHandle()
    {
        lock (_sync)
        {
            _activeWaiters--;
            TryDisposeEvent();
        }
    }

    internal void ThrowSubmissionExceptionIfSignaled()
    {
        lock (_sync)
        {
            if (_mre.WaitOne(0))
            {
                ThrowSubmissionException();
            }
        }
    }

    private void ThrowSubmissionException()
    {
        Exception exception;
        lock (_sync)
        {
            exception = _submissionException;
        }

        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private void TryDisposeEvent()
    {
        if (_disposeRequested
            && !_submissionPending
            && _activeWaiters == 0
            && !_eventDisposed)
        {
            _mre.Dispose();
            _eventDisposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposeRequested)
        {
            throw new ObjectDisposedException(nameof(OpenGLFence));
        }
    }
}
