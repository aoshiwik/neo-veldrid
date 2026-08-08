#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace NeoVeldrid.Sdl2;

internal enum Sdl2WindowCloseResult
{
    AlreadyClosing,
    Vetoed,
    Closed,
}

internal sealed class Sdl2WindowCloseCoordinator
{
    private const int CloseAvailable = 0;
    private const int CloseClaimed = 1;

    private int _closeState;

    public Sdl2WindowCloseResult TryClose(
        Func<bool>? closeRequestedHandler,
        Action unregisterWindow,
        Func<Action?> getClosingObservers,
        Action destroyWindow,
        Func<Action?> getClosedObservers)
    {
        ArgumentNullException.ThrowIfNull(unregisterWindow);
        ArgumentNullException.ThrowIfNull(getClosingObservers);
        ArgumentNullException.ThrowIfNull(destroyWindow);
        ArgumentNullException.ThrowIfNull(getClosedObservers);

        if (Interlocked.CompareExchange(
            ref _closeState,
            CloseClaimed,
            CloseAvailable) != CloseAvailable)
        {
            return Sdl2WindowCloseResult.AlreadyClosing;
        }

        bool closeCommitted = false;
        try
        {
            List<Exception>? failures = null;
            bool closeVetoed = InvokeCloseRequestedHandler(
                closeRequestedHandler,
                ref failures);
            if (closeVetoed)
            {
                return Sdl2WindowCloseResult.Vetoed;
            }

            closeCommitted = true;
            InvokeOperation(unregisterWindow, ref failures);
            InvokeObservers(getClosingObservers, ref failures);
            InvokeOperation(destroyWindow, ref failures);
            InvokeObservers(getClosedObservers, ref failures);
            ThrowFailures(failures);
            return Sdl2WindowCloseResult.Closed;
        }
        finally
        {
            if (!closeCommitted)
            {
                Volatile.Write(ref _closeState, CloseAvailable);
            }
        }
    }

    private static bool InvokeCloseRequestedHandler(
        Func<bool>? closeRequestedHandler,
        ref List<Exception>? failures)
    {
        if (closeRequestedHandler == null)
        {
            return false;
        }

        try
        {
            return closeRequestedHandler();
        }
        catch (Exception failure)
        {
            AddFailure(failure, ref failures);
            // A broken policy callback cannot be allowed to orphan the native
            // window. Continue closing and report the failure afterwards.
            return false;
        }
    }

    private static void InvokeOperation(
        Action operation,
        ref List<Exception>? failures)
    {
        try
        {
            operation();
        }
        catch (Exception failure)
        {
            AddFailure(failure, ref failures);
        }
    }

    private static void InvokeObservers(
        Func<Action?> getObservers,
        ref List<Exception>? failures)
    {
        Action? observers;
        try
        {
            observers = getObservers();
        }
        catch (Exception failure)
        {
            AddFailure(failure, ref failures);
            return;
        }

        if (observers == null)
        {
            return;
        }

        foreach (Action observer in observers.GetInvocationList())
        {
            InvokeOperation(observer, ref failures);
        }
    }

    private static void AddFailure(
        Exception failure,
        ref List<Exception>? failures)
    {
        (failures ??= new List<Exception>()).Add(failure);
    }

    private static void ThrowFailures(List<Exception>? failures)
    {
        if (failures?.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures is { Count: > 1 })
        {
            throw new AggregateException(
                "SDL window close encountered multiple failures.",
                failures);
        }
    }
}
