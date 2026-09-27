using System.Collections.Generic;

namespace Hkmp.Networking;

/// <summary>
/// Bounded set of the most recently added sequence numbers. Not thread-safe.
/// </summary>
internal class SequenceHistory {
    /// <summary>
    /// The maximum number of sequence numbers to remember.
    /// </summary>
    private readonly int _size;

    /// <summary>
    /// The sequence numbers in the order they were added.
    /// </summary>
    private readonly Queue<ushort> _order = new();

    /// <summary>
    /// The same sequence numbers as in <see cref="_order"/> for fast lookup.
    /// </summary>
    private readonly HashSet<ushort> _set = new();

    public SequenceHistory(int size) {
        _size = size;
    }

    /// <summary>
    /// Add the given sequence number, forgetting the oldest one if the history is full.
    /// </summary>
    /// <param name="sequence">The sequence number to add.</param>
    public void Add(ushort sequence) {
        if (!_set.Add(sequence)) {
            return;
        }

        _order.Enqueue(sequence);
        while (_order.Count > _size) {
            _set.Remove(_order.Dequeue());
        }
    }

    /// <summary>
    /// Whether the given sequence number is in the history.
    /// </summary>
    /// <param name="sequence">The sequence number to check.</param>
    /// <returns>True if the sequence number was added and not forgotten yet, false otherwise.</returns>
    public bool Contains(ushort sequence) => _set.Contains(sequence);
}
