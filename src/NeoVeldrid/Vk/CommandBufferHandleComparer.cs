using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace NeoVeldrid.Vk;

/// <summary>
/// Compares Vulkan command-buffer wrappers by their native handle without
/// falling back to allocating <see cref="System.ValueType"/> equality.
/// </summary>
internal sealed class CommandBufferHandleComparer : IEqualityComparer<CommandBuffer>
{
    public static CommandBufferHandleComparer Instance { get; } = new CommandBufferHandleComparer();

    private CommandBufferHandleComparer()
    {
    }

    public bool Equals(CommandBuffer x, CommandBuffer y)
        => x.Handle == y.Handle;

    public int GetHashCode(CommandBuffer value)
        => value.Handle.GetHashCode();
}
