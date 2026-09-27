using System;
using System.Collections.Generic;

namespace Hkmp.Networking.Packet;

/// <summary>
/// Splits packets that exceed the MTU into fragments and reassembles received fragments into packets. Every
/// fragment carries the ID of the packet it belongs to and its index, so fragments may arrive in any order, and the
/// fragments of a packet that can not be completed because one of them was lost are eventually discarded.
/// </summary>
internal class PacketFragments {
    /// <summary>
    /// Value at the start of a fragment. A whole packet starts with its length there instead, which is always
    /// lower since whole packets do not exceed the MTU.
    /// </summary>
    private const ushort FragmentMarker = ushort.MaxValue;

    /// <summary>
    /// The size of the fragment header: marker (ushort), packet ID (ushort), fragment index (byte) and number of
    /// fragments (byte).
    /// </summary>
    private const int HeaderSize = 6;

    /// <summary>
    /// The maximum number of incomplete packets to keep fragments for, the oldest is discarded beyond that.
    /// </summary>
    private const int MaxIncompletePackets = 16;

    /// <summary>
    /// Received fragments of incomplete packets keyed by packet ID.
    /// </summary>
    private readonly Dictionary<ushort, byte[][]> _incomplete = new();

    /// <summary>
    /// The IDs of incomplete packets in the order they were first received.
    /// </summary>
    private readonly Queue<ushort> _incompleteOrder = new();

    /// <summary>
    /// Split the given packet bytes into fragments that each fit in the given MTU including their header.
    /// </summary>
    /// <param name="packet">The bytes of the packet, including its length.</param>
    /// <param name="packetId">The ID for this packet, which should differ from recently split packets.</param>
    /// <param name="mtu">The maximum size of a fragment.</param>
    /// <returns>The fragments in order.</returns>
    public static List<byte[]> Split(byte[] packet, ushort packetId, int mtu) {
        var partSize = mtu - HeaderSize;
        var numFragments = (packet.Length + partSize - 1) / partSize;
        if (numFragments > byte.MaxValue) {
            throw new ArgumentException($"Packet of {packet.Length} bytes needs too many fragments");
        }

        var fragments = new List<byte[]>(numFragments);
        for (var i = 0; i < numFragments; i++) {
            var offset = i * partSize;
            var length = System.Math.Min(partSize, packet.Length - offset);

            var fragment = new byte[HeaderSize + length];
            BitConverter.GetBytes(FragmentMarker).CopyTo(fragment, 0);
            BitConverter.GetBytes(packetId).CopyTo(fragment, 2);
            fragment[4] = (byte) i;
            fragment[5] = (byte) numFragments;
            Array.Copy(packet, offset, fragment, HeaderSize, length);

            fragments.Add(fragment);
        }

        return fragments;
    }

    /// <summary>
    /// Whether the given received data is a fragment rather than a whole packet.
    /// </summary>
    /// <param name="buffer">The buffer with the received data.</param>
    /// <param name="length">The number of received bytes in the buffer.</param>
    /// <returns>True if the data is a fragment, false otherwise.</returns>
    public static bool IsFragment(byte[] buffer, int length) {
        return length >= HeaderSize && BitConverter.ToUInt16(buffer, 0) == FragmentMarker;
    }

    /// <summary>
    /// Add a received fragment.
    /// </summary>
    /// <param name="buffer">The buffer with the received fragment.</param>
    /// <param name="length">The number of received bytes in the buffer.</param>
    /// <returns>The bytes of the packet if this fragment completed it, null otherwise.</returns>
    public byte[] Add(byte[] buffer, int length) {
        var packetId = BitConverter.ToUInt16(buffer, 2);
        var index = buffer[4];
        var numFragments = buffer[5];
        if (numFragments < 2 || index >= numFragments) {
            return null;
        }

        if (!_incomplete.TryGetValue(packetId, out var fragments) || fragments.Length != numFragments) {
            fragments = new byte[numFragments][];
            if (!_incomplete.ContainsKey(packetId)) {
                _incompleteOrder.Enqueue(packetId);
            }

            _incomplete[packetId] = fragments;

            while (_incompleteOrder.Count > MaxIncompletePackets) {
                _incomplete.Remove(_incompleteOrder.Dequeue());
            }
        }

        var part = new byte[length - HeaderSize];
        Array.Copy(buffer, HeaderSize, part, 0, part.Length);
        fragments[index] = part;

        var totalLength = 0;
        foreach (var fragment in fragments) {
            if (fragment == null) {
                return null;
            }

            totalLength += fragment.Length;
        }

        _incomplete.Remove(packetId);

        var packet = new byte[totalLength];
        var offset = 0;
        foreach (var fragment in fragments) {
            fragment.CopyTo(packet, offset);
            offset += fragment.Length;
        }

        return packet;
    }
}
