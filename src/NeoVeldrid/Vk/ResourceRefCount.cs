using System;
using System.Threading;

namespace NeoVeldrid.Vk;

internal class ResourceRefCount
{
    private readonly Action _disposeAction;
    private int _refCount;

    public ResourceRefCount(Action disposeAction)
    {
        _disposeAction = disposeAction
            ?? throw new ArgumentNullException(nameof(disposeAction));
        _refCount = 1;
    }

    /// <summary>
    /// Gets the currently-held native ownership count. This is an internal
    /// lifecycle diagnostic; it does not itself acquire a reference.
    /// </summary>
    internal int CurrentCount => Volatile.Read(ref _refCount);

    public int Increment()
    {
        int ret = Interlocked.Increment(ref _refCount);
#if VALIDATE_USAGE
        if (ret == 0)
        {
            throw new NeoVeldridException("An attempt was made to reference a disposed resource.");
        }
#endif
        return ret;
    }

    public int Decrement()
    {
        int ret = Interlocked.Decrement(ref _refCount);
        if (ret == 0)
        {
            try
            {
                _disposeAction();
            }
            catch
            {
                // Disposal actions in the Vulkan backend retire ownership
                // graphs incrementally and can report a cleanup failure. Keep
                // the releasing reference alive so the remaining graph can be
                // retried instead of stranding the counter at zero forever.
                Interlocked.CompareExchange(ref _refCount, 1, 0);
                throw;
            }
        }

        return ret;
    }
}
