using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoVeldrid.OpenGL;

internal unsafe sealed class StagingMemoryPool : IDisposable
{
    private const uint MinimumCapacity = 128;

    private readonly List<StagingBlock> _storage;
    private readonly List<StagingBlockState> _states;
    private readonly SortedList<uint, uint> _availableBlocks;
    private readonly object _lock = new object();
    private bool _disposed;

    public StagingMemoryPool()
    {
        _storage = new List<StagingBlock>();
        _states = new List<StagingBlockState>();
        _availableBlocks = new SortedList<uint, uint>(new CapacityComparer());
    }

    public StagingBlock Stage(IntPtr source, uint sizeInBytes)
    {
        Rent(sizeInBytes, out StagingBlock block);
        try
        {
            Unsafe.CopyBlock(block.Data, source.ToPointer(), sizeInBytes);
            return block;
        }
        catch
        {
            Free(block);
            throw;
        }
    }

    public StagingBlock Stage(byte[] bytes)
    {
        Rent((uint)bytes.Length, out StagingBlock block);
        try
        {
            Marshal.Copy(bytes, 0, (IntPtr)block.Data, bytes.Length);
            return block;
        }
        catch
        {
            Free(block);
            throw;
        }
    }

    public StagingBlock GetStagingBlock(uint sizeInBytes)
    {
        Rent(sizeInBytes, out StagingBlock block);
        return block;
    }

    public StagingBlock RetrieveById(uint id)
    {
        lock (_lock)
        {
            int index = checked((int)id);
            if ((uint)index >= (uint)_storage.Count
                || _states[index] != StagingBlockState.Rented)
            {
                throw new InvalidOperationException(
                    $"Staging block {id} is not owned by an active upload.");
            }

            return _storage[index];
        }
    }

    private void Rent(uint size, out StagingBlock block)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            SortedList<uint, uint> available = _availableBlocks;
            IList<uint> indices = available.Values;
            for (int i = 0; i < available.Count; i++)
            {
                int index = (int)indices[i];
                StagingBlock current = _storage[index];
                if (current.Capacity >= size)
                {
                    available.RemoveAt(i);
                    current.SizeInBytes = size;
                    block = current;
                    _storage[index] = current;
                    _states[index] = StagingBlockState.Rented;
                    return;
                }
            }

            Allocate(size, out block);
        }
    }

    private void Allocate(uint sizeInBytes, out StagingBlock stagingBlock)
    {
        uint capacity = Math.Max(MinimumCapacity, sizeInBytes);
        IntPtr ptr = Marshal.AllocHGlobal((int)capacity);
        uint id = (uint)_storage.Count;
        stagingBlock = new StagingBlock(id, (void*)ptr, capacity, sizeInBytes);
        _storage.Add(stagingBlock);
        _states.Add(StagingBlockState.Rented);
    }

    public void Free(StagingBlock block)
    {
        lock (_lock)
        {
            int index = checked((int)block.Id);
            if ((uint)index >= (uint)_storage.Count
                || _states[index] != StagingBlockState.Rented
                || _storage[index].Data != block.Data)
            {
                throw new InvalidOperationException(
                    $"Staging block {block.Id} is not owned by an active upload.");
            }

            if (_disposed)
            {
                Marshal.FreeHGlobal((IntPtr)block.Data);
                _states[index] = StagingBlockState.Freed;
            }
            else
            {
                _availableBlocks.Add(block.Capacity, block.Id);
                _states[index] = StagingBlockState.Available;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _availableBlocks.Clear();
            for (int i = 0; i < _storage.Count; i++)
            {
                if (_states[i] == StagingBlockState.Available)
                {
                    Marshal.FreeHGlobal((IntPtr)_storage[i].Data);
                    _states[i] = StagingBlockState.Freed;
                }
            }
            _disposed = true;
        }
    }

    private enum StagingBlockState : byte
    {
        Rented,
        Available,
        Freed,
    }

    private class CapacityComparer : IComparer<uint>
    {
        public int Compare(uint x, uint y)
        {
            return x >= y ? 1 : -1;
        }
    }
}

internal unsafe struct StagingBlock
{
    public readonly uint Id;
    public readonly void* Data;
    public readonly uint Capacity;
    public uint SizeInBytes;

    public StagingBlock(uint id, void* data, uint capacity, uint size)
    {
        Id = id;
        Data = data;
        Capacity = capacity;
        SizeInBytes = size;
    }
}
