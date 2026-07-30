using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LubanDesktopPet;

/// <summary>
/// Reuses sprite-page pixel buffers in one-mebibyte capacity buckets across
/// background decodes.
/// </summary>
/// <remarks>
/// All members are thread-safe. A rented buffer represents either an in-flight
/// decode or a resident atlas page; the pool deliberately does not distinguish
/// those two owner states. Rented and free buffers both count toward
/// <see cref="AllocatedBytes"/>.
///
/// The budget is a retention target rather than a reason for an animation to
/// fail. Before allocating, the pool discards free buffers until the request
/// fits when possible. If rented buffers already consume too much of the
/// budget, one necessary page allocation is still allowed. Returning buffers
/// automatically discards free storage until the pool converges to the budget
/// again. With the application's single background page decoder, this limits
/// the unavoidable transient excess to one page.
///
/// Buffers are never cleared here because sprite decoding completely
/// overwrites the requested array. This type is intended for page
/// decode/eviction boundaries and must not be called from the per-frame render
/// callback.
/// </remarks>
internal sealed class SpritePageBufferPool
{
    internal const long DefaultHardBudgetBytes = 128L * 1024 * 1024;
    internal const int CapacityBucketBytes = 1 * 1024 * 1024;

    private readonly object _syncRoot = new();
    private readonly long _hardBudgetBytes;
    private readonly Dictionary<int, Stack<byte[]>> _freeBuffersByCapacity = [];
    private readonly Dictionary<byte[], BufferState> _bufferStates =
        new(ByteArrayReferenceComparer.Instance);

    private long _allocatedBytes;
    private long _rentedBytes;
    private long _freeBytes;
    private long _allocationCount;
    private long _reuseCount;

    public SpritePageBufferPool()
        : this(DefaultHardBudgetBytes)
    {
    }

    public SpritePageBufferPool(long hardBudgetBytes)
    {
        if (hardBudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hardBudgetBytes),
                hardBudgetBytes,
                "The sprite-page buffer budget must be greater than zero.");
        }

        _hardBudgetBytes = hardBudgetBytes;
    }

    public long HardBudgetBytes => _hardBudgetBytes;

    public long AllocatedBytes
    {
        get
        {
            lock (_syncRoot)
            {
                return _allocatedBytes;
            }
        }
    }

    public long RentedBytes
    {
        get
        {
            lock (_syncRoot)
            {
                return _rentedBytes;
            }
        }
    }

    public long FreeBytes
    {
        get
        {
            lock (_syncRoot)
            {
                return _freeBytes;
            }
        }
    }

    public long AllocationCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _allocationCount;
            }
        }
    }

    public long ReuseCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _reuseCount;
            }
        }
    }

    /// <summary>
    /// Rents a buffer whose <see cref="Array.Length"/> is at least
    /// <paramref name="length"/>. Capacity is rounded up to the next
    /// one-mebibyte bucket so differently sized pages in the same bucket can
    /// reuse one another's storage.
    /// </summary>
    public byte[] Rent(int length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "A sprite-page buffer length must be greater than zero.");
        }

        var capacity = GetCapacity(length);
        lock (_syncRoot)
        {
            if (TryRentFreeBuffer(capacity, out var reusedBuffer))
            {
                return reusedBuffer;
            }

            // Free buffers are the only storage that can be discarded safely.
            // Rented buffers may still be decoded or displayed and must never
            // be invalidated merely to satisfy the soft retention target.
            var targetBeforeAllocation = Math.Max(
                0L,
                _hardBudgetBytes - capacity);
            _ = TrimFreeBuffersCore(targetBeforeAllocation);

            // Allocation remains inside the accounting lock so concurrent Rent
            // calls cannot all observe the same unused budget. Large page
            // allocation is never performed by the render callback.
            var buffer = new byte[capacity];
            if (!_bufferStates.TryAdd(buffer, BufferState.Rented))
            {
                throw new InvalidOperationException(
                    "A newly allocated sprite-page buffer was already tracked.");
            }

            _allocatedBytes = checked(_allocatedBytes + capacity);
            _rentedBytes = checked(_rentedBytes + capacity);
            _allocationCount = checked(_allocationCount + 1);
            ValidateAccounting();
            return buffer;
        }
    }

    /// <summary>
    /// Returns a previously rented buffer without clearing its contents.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The buffer was not created by this pool or has already been returned.
    /// </exception>
    public void Return(byte[] buffer)
    {
        _ = ReturnAndGetDiscardedBytes(buffer);
    }

    /// <summary>
    /// Returns a previously rented buffer and reports storage discarded while
    /// converging back to the configured budget.
    /// </summary>
    /// <remarks>
    /// The returned byte count is useful only for low-frequency memory
    /// accounting. Callers must not use it to drive per-frame work.
    /// </remarks>
    public long ReturnAndGetDiscardedBytes(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        lock (_syncRoot)
        {
            if (!_bufferStates.TryGetValue(buffer, out var state) ||
                state != BufferState.Rented)
            {
                throw new InvalidOperationException(
                    "The sprite-page buffer does not belong to this pool or " +
                    "has already been returned.");
            }

            _bufferStates[buffer] = BufferState.Free;
            _rentedBytes = checked(_rentedBytes - buffer.LongLength);
            _freeBytes = checked(_freeBytes + buffer.LongLength);

            if (!_freeBuffersByCapacity.TryGetValue(
                    buffer.Length,
                    out var freeBuffers))
            {
                freeBuffers = new Stack<byte[]>();
                _freeBuffersByCapacity.Add(buffer.Length, freeBuffers);
            }

            freeBuffers.Push(buffer);

            // If an indispensable Rent temporarily crossed the budget, the
            // first available returned buffers make the pool converge again.
            var discardedBytes =
                TrimFreeBuffersCore(_hardBudgetBytes);
            ValidateAccounting();
            return discardedBytes;
        }
    }

    /// <summary>
    /// Discards free buffers until total tracked storage is at or below the
    /// configured hard budget, or until only rented buffers remain.
    /// </summary>
    /// <returns>The number of bytes removed from the pool.</returns>
    public long TrimFreeBuffers()
    {
        return TrimFreeBuffers(_hardBudgetBytes);
    }

    /// <summary>
    /// Discards free buffers until total tracked storage is at or below
    /// <paramref name="targetAllocatedBytes"/>, or until only rented buffers
    /// remain. Rented buffers are never invalidated.
    /// </summary>
    /// <returns>The number of bytes removed from the pool.</returns>
    public long TrimFreeBuffers(long targetAllocatedBytes)
    {
        if (targetAllocatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetAllocatedBytes),
                targetAllocatedBytes,
                "The trim target cannot be negative.");
        }

        lock (_syncRoot)
        {
            var removedBytes = TrimFreeBuffersCore(targetAllocatedBytes);
            ValidateAccounting();
            return removedBytes;
        }
    }

    /// <summary>
    /// Discards every free buffer while preserving all in-flight and resident
    /// rented buffers.
    /// </summary>
    /// <returns>The number of bytes removed from the pool.</returns>
    public long ClearFreeBuffers()
    {
        lock (_syncRoot)
        {
            var removedBytes = TrimFreeBuffersCore(0);
            ValidateAccounting();
            return removedBytes;
        }
    }

    private static int GetCapacity(int requestedLength)
    {
        var capacity = checked(
            ((long)requestedLength + CapacityBucketBytes - 1) /
            CapacityBucketBytes *
            CapacityBucketBytes);
        if (capacity > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedLength),
                requestedLength,
                "The rounded sprite-page buffer capacity exceeds the maximum " +
                "supported array length.");
        }

        return (int)capacity;
    }

    private bool TryRentFreeBuffer(int capacity, out byte[] buffer)
    {
        if (!_freeBuffersByCapacity.TryGetValue(
                capacity,
                out var freeBuffers) ||
            freeBuffers.Count == 0)
        {
            buffer = null!;
            return false;
        }

        buffer = freeBuffers.Pop();
        if (freeBuffers.Count == 0)
        {
            _freeBuffersByCapacity.Remove(capacity);
        }

        if (!_bufferStates.TryGetValue(buffer, out var state) ||
            state != BufferState.Free ||
            buffer.Length != capacity)
        {
            throw new InvalidOperationException(
                "The sprite-page buffer pool free-list state is inconsistent.");
        }

        _bufferStates[buffer] = BufferState.Rented;
        _freeBytes = checked(_freeBytes - buffer.LongLength);
        _rentedBytes = checked(_rentedBytes + buffer.LongLength);
        _reuseCount = checked(_reuseCount + 1);
        ValidateAccounting();
        return true;
    }

    private long TrimFreeBuffersCore(long targetAllocatedBytes)
    {
        var removedBytes = 0L;
        while (_allocatedBytes > targetAllocatedBytes && _freeBytes > 0)
        {
            var selectedCapacity = FindLargestFreeBufferCapacity();
            if (selectedCapacity <= 0 ||
                !_freeBuffersByCapacity.TryGetValue(
                    selectedCapacity,
                    out var freeBuffers) ||
                freeBuffers.Count == 0)
            {
                throw new InvalidOperationException(
                    "The sprite-page buffer pool byte accounting is inconsistent.");
            }

            var buffer = freeBuffers.Pop();
            if (freeBuffers.Count == 0)
            {
                _freeBuffersByCapacity.Remove(selectedCapacity);
            }

            if (!_bufferStates.Remove(buffer, out var state) ||
                state != BufferState.Free)
            {
                throw new InvalidOperationException(
                    "The sprite-page buffer pool attempted to trim a rented buffer.");
            }

            _freeBytes = checked(_freeBytes - buffer.LongLength);
            _allocatedBytes = checked(_allocatedBytes - buffer.LongLength);
            removedBytes = checked(removedBytes + buffer.LongLength);
        }

        return removedBytes;
    }

    private int FindLargestFreeBufferCapacity()
    {
        var selectedCapacity = 0;
        foreach (var (capacity, buffers) in _freeBuffersByCapacity)
        {
            if (buffers.Count > 0 && capacity > selectedCapacity)
            {
                selectedCapacity = capacity;
            }
        }

        return selectedCapacity;
    }

    private void ValidateAccounting()
    {
        if (_allocatedBytes < 0 ||
            _rentedBytes < 0 ||
            _freeBytes < 0 ||
            _allocatedBytes != _rentedBytes + _freeBytes)
        {
            throw new InvalidOperationException(
                "The sprite-page buffer pool byte accounting is inconsistent.");
        }
    }

    private enum BufferState : byte
    {
        Rented,
        Free
    }

    private sealed class ByteArrayReferenceComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayReferenceComparer Instance { get; } = new();

        public bool Equals(byte[]? left, byte[]? right) =>
            ReferenceEquals(left, right);

        public int GetHashCode(byte[] value) =>
            RuntimeHelpers.GetHashCode(value);
    }
}
