using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace NeoVeldrid.Vk;

/// <summary>
/// Collects independent failures while a Vulkan ownership graph is retired.
/// Callers remain responsible for invoking actions in child-before-parent order.
/// </summary>
internal sealed class VulkanCleanupCollector
{
    private readonly List<Exception> _failures = new List<Exception>();

    internal bool Attempt(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);

        try
        {
            cleanup();
            return true;
        }
        catch (Exception exception)
        {
            _failures.Add(exception);
            return false;
        }
    }

    internal void Add(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        _failures.Add(failure);
    }

    internal void ThrowIfAny(string message)
    {
        if (_failures.Count == 0)
        {
            return;
        }

        if (_failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(_failures[0]).Throw();
            throw new UnreachableException();
        }

        throw new AggregateException(message, _failures);
    }

    [DoesNotReturn]
    internal void ThrowWithPrimary(Exception primaryFailure, string message)
    {
        ArgumentNullException.ThrowIfNull(primaryFailure);

        if (_failures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw new UnreachableException();
        }

        List<Exception> combined = new List<Exception>(_failures.Count + 1)
        {
            primaryFailure
        };
        combined.AddRange(_failures);
        throw new AggregateException(message, combined);
    }
}
