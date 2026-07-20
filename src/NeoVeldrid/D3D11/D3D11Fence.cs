using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using System;
using System.Threading;

namespace NeoVeldrid.D3D11;

/// <summary>
/// A D3D11 EVENT query used as a GPU-to-CPU completion point.
/// </summary>
internal unsafe sealed class D3D11Fence : Fence
{
    private readonly D3D11GraphicsDevice _gd;
    private readonly object _sync = new object();
    private ComPtr<ID3D11Query> _query;
    private bool _submitted;
    private bool _signaled;
    private bool _disposed;
    private string _name;

    public D3D11Fence(D3D11GraphicsDevice gd, bool signaled)
    {
        _gd = gd ?? throw new ArgumentNullException(nameof(gd));
        _signaled = signaled;

        QueryDesc queryDescription = new QueryDesc
        {
            Query = Silk.NET.Direct3D11.Query.Event,
            MiscFlags = 0,
        };
        ID3D11Query* query = null;
        try
        {
            SilkMarshal.ThrowHResult(
                gd.Device->CreateQuery(&queryDescription, &query));
            _query = default;
            _query.Handle = query;
            query = null;
        }
        finally
        {
            // Failed COM calls may still populate output pointers.
            if (query != null)
            {
                query->Release();
            }
        }
    }

    public override string Name
    {
        get
        {
            lock (_sync)
            {
                return _name;
            }
        }
        set
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _name = value;
                D3D11Util.SetDebugName((ID3D11DeviceChild*)_query.Handle, value);
            }
        }
    }

    public override bool Signaled => _gd.IsFenceSignaled(this);

    public override bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposed;
            }
        }
    }

    /// <summary>
    /// Verifies that a graphics-device operation is targeting the device which
    /// created this native query.
    /// </summary>
    internal void ValidateOwner(D3D11GraphicsDevice graphicsDevice)
    {
        if (!ReferenceEquals(_gd, graphicsDevice))
        {
            throw new InvalidOperationException(
                "The Direct3D 11 fence belongs to a different graphics device.");
        }
    }

    /// <summary>
    /// Reserves the fence from submission preflight until its native EVENT
    /// marker has been inserted. This prevents concurrent disposal from
    /// splitting a committed command-list execution from its completion point.
    /// The graphics-device immediate-context lock must be held by the caller.
    /// </summary>
    internal void AcquireSubmission(D3D11GraphicsDevice graphicsDevice)
    {
        Monitor.Enter(_sync);
        try
        {
            ValidateOwner(graphicsDevice);
            ThrowIfDisposed();
            if (_submitted)
            {
                throw new InvalidOperationException(
                    "A Direct3D 11 fence cannot be submitted again while its previous GPU event is pending.");
            }
            if (_signaled)
            {
                throw new InvalidOperationException(
                    "A signaled Direct3D 11 fence must be reset before it can be submitted again.");
            }
        }
        catch
        {
            Monitor.Exit(_sync);
            throw;
        }
    }

    /// <summary>
    /// Inserts the native EVENT marker while the caller owns the submission
    /// reservation acquired by <see cref="AcquireSubmission"/>.
    /// </summary>
    internal void ArmReserved(ID3D11DeviceContext* context)
    {
        if (!Monitor.IsEntered(_sync))
        {
            throw new InvalidOperationException(
                "A Direct3D 11 fence must be reserved before it can be armed.");
        }

        ThrowIfDisposed();
        context->End((ID3D11Asynchronous*)_query.Handle);
        _submitted = true;
    }

    internal void ReleaseSubmission()
    {
        if (!Monitor.IsEntered(_sync))
        {
            throw new InvalidOperationException(
                "The current thread does not own the Direct3D 11 fence submission reservation.");
        }

        Monitor.Exit(_sync);
    }

    /// <summary>
    /// Polls the event query. The graphics-device immediate-context lock must
    /// be held by the caller.
    /// </summary>
    internal bool Poll(ID3D11DeviceContext* context)
    {
        lock (_sync)
        {
            return PollLocked(context);
        }
    }

    public override void Reset()
    {
        _gd.ResetFenceState(this);
    }

    /// <summary>
    /// Resets a completed event query for reuse. The graphics-device
    /// immediate-context lock must be held by the caller.
    /// </summary>
    internal void ResetForReuse(ID3D11DeviceContext* context)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_submitted && !PollLocked(context))
            {
                throw new InvalidOperationException(
                    "A Direct3D 11 fence cannot be reset while its GPU event is pending.");
            }

            _signaled = false;
        }
    }

    public override void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _query.Dispose();
            _query = default;
        }
    }

    private bool PollLocked(ID3D11DeviceContext* context)
    {
        ThrowIfDisposed();
        if (_signaled)
        {
            return true;
        }
        if (!_submitted)
        {
            return false;
        }

        int result = context->GetData(
            (ID3D11Asynchronous*)_query.Handle,
            null,
            0,
            (uint)AsyncGetdataFlag.Donotflush);
        if (result == 0) // S_OK
        {
            _submitted = false;
            _signaled = true;
            return true;
        }
        if (result == 1) // S_FALSE
        {
            return false;
        }
        if (result < 0)
        {
            SilkMarshal.ThrowHResult(result);
        }

        throw new NeoVeldridException(
            $"ID3D11DeviceContext.GetData returned unexpected fence status 0x{result:X8}.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(D3D11Fence));
        }
    }
}
