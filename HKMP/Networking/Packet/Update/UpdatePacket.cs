using System;
using System.Collections.Generic;
using Hkmp.Logging;
using Hkmp.Networking.Packet.Data;

namespace Hkmp.Networking.Packet.Update;

/// <summary>
/// Abstract base class for the update packet.
/// </summary>
/// <typeparam name="TPacketId"></typeparam>
internal abstract class UpdatePacket<TPacketId> : BasePacket<TPacketId> where TPacketId : Enum {
    /// <summary>
    /// The sequence number of this packet.
    /// </summary>
    public ushort Sequence { get; set; }

    /// <summary>
    /// The acknowledgement number of this packet.
    /// </summary>
    public ushort Ack { get; set; }

    /// <summary>
    /// An array containing booleans that indicate whether sequence number (Ack - x) is also acknowledged
    /// for the x-th value in the array.
    /// </summary>
    public bool[] AckField { get; private set; }
    
    /// <summary>
    /// Resend packet data indexed by sequence number it originates from.
    /// </summary>
    protected readonly Dictionary<ushort, Dictionary<TPacketId, IPacketData>> ResendPacketData;
    
    /// <summary>
    /// Resend addon packet data indexed by sequence number it originates from.
    /// </summary>
    protected readonly Dictionary<ushort, Dictionary<byte, AddonPacketData>> ResendAddonPacketData;

    /// <summary>
    /// The maximum size in bytes of resent data in a single packet. As long as reliable data keeps getting lost, it
    /// would otherwise all be resent in every packet, making packets ever larger and thus ever more likely to be
    /// lost themselves. Resent data beyond this size is deferred to the next packet.
    /// </summary>
    private const int MaxResendDataSize = 8192;

    /// <summary>
    /// Resend entries of packet data that did not fit in this packet when it was created.
    /// </summary>
    private readonly Dictionary<ushort, Dictionary<TPacketId, IPacketData>> _deferredResendPacketData = new();

    /// <summary>
    /// Resend entries of addon data that did not fit in this packet when it was created.
    /// </summary>
    private readonly Dictionary<ushort, Dictionary<byte, AddonPacketData>> _deferredResendAddonPacketData = new();
    
    protected UpdatePacket() {
        AckField = new bool[UdpUpdateManager.AckSize];
        
        ResendPacketData = new Dictionary<ushort, Dictionary<TPacketId, IPacketData>>();
        ResendAddonPacketData = new Dictionary<ushort, Dictionary<byte, AddonPacketData>>();
    }
    
    /// <summary>
    /// Write header info into the given packet (sequence number, acknowledgement number and ack field).
    /// </summary>
    /// <param name="packet">The packet to write the header info into.</param>
    private void WriteHeaders(Packet packet) {
        packet.Write(Sequence);
        packet.Write(Ack);

        ulong ackFieldInt = 0;
        ulong currentFieldValue = 1;
        for (var i = 0; i < UdpUpdateManager.AckSize; i++) {
            if (AckField[i]) {
                ackFieldInt |= currentFieldValue;
            }

            currentFieldValue *= 2;
        }

        packet.Write(ackFieldInt);
    }

    /// <summary>
    /// Read header info from the given packet (sequence number, acknowledgement number and ack field).
    /// </summary>
    /// <param name="packet">The packet to read header info from.</param>
    private void ReadHeaders(Packet packet) {
        Sequence = packet.ReadUShort();
        Ack = packet.ReadUShort();

        // Initialize the AckField array
        AckField = new bool[UdpUpdateManager.AckSize];

        var ackFieldInt = packet.ReadULong();
        ulong currentFieldValue = 1;
        for (var i = 0; i < UdpUpdateManager.AckSize; i++) {
            AckField[i] = (ackFieldInt & currentFieldValue) != 0;

            currentFieldValue *= 2;
        }
    }

    /// <inheritdoc />
    public override void CreatePacket(Packet packet) {
        WriteHeaders(packet);
        
        base.CreatePacket(packet);
        
        // Add the entries of lost data to resend, then the entries of lost addon data to resend
        var resendDataSize = 0;
        WriteResendData(packet, ResendPacketData, _deferredResendPacketData, WritePacketData, ref resendDataSize);
        WriteResendData(
            packet,
            ResendAddonPacketData,
            _deferredResendAddonPacketData,
            WriteAddonDataDict,
            ref resendDataSize
        );
        
        packet.WriteLength();
    }

    /// <summary>
    /// Write the given resend entries into the packet, preceded by their count, as long as the total size of resent
    /// data stays within <see cref="MaxResendDataSize"/>. Entries that do not fit are removed from this packet and
    /// kept to be moved to the next packet with <see cref="MoveDeferredResendData"/>.
    /// </summary>
    /// <param name="packet">The raw packet instance to write into.</param>
    /// <param name="resendData">The resend entries keyed by the sequence number they were originally sent with.
    /// </param>
    /// <param name="deferredData">The dictionary to put entries in that do not fit.</param>
    /// <param name="writeData">Function that writes the data of an entry into a packet.</param>
    /// <param name="resendDataSize">The size of resent data written into the packet so far.</param>
    /// <typeparam name="TData">The type of the data of an entry.</typeparam>
    private void WriteResendData<TData>(
        Packet packet,
        Dictionary<ushort, TData> resendData,
        Dictionary<ushort, TData> deferredData,
        Func<Packet, TData, bool> writeData,
        ref int resendDataSize
    ) {
        var entries = new List<byte[]>();

        foreach (var seqDataPair in resendData) {
            var entryPacket = new Packet();

            // First write the sequence number it belongs to, then the data itself
            entryPacket.Write(seqDataPair.Key);
            writeData(entryPacket, seqDataPair.Value);

            // The first entry is always written, so an entry larger than the maximum still gets through
            if (resendDataSize > 0 && resendDataSize + entryPacket.Length > MaxResendDataSize
                || entries.Count == ushort.MaxValue) {
                deferredData[seqDataPair.Key] = seqDataPair.Value;
                continue;
            }

            resendDataSize += entryPacket.Length;
            entries.Add(entryPacket.ToArray());
        }

        foreach (var seq in deferredData.Keys) {
            resendData.Remove(seq);
        }

        packet.Write((ushort) entries.Count);
        foreach (var entry in entries) {
            packet.Write(entry);
        }

        // Note that this packet now contains reliable data
        if (entries.Count > 0) {
            ContainsReliableData = true;
        }
    }

    /// <summary>
    /// Move the resend data that did not fit in this packet when it was created to the given packet.
    /// </summary>
    /// <param name="nextPacket">The update packet that will be sent after this one.</param>
    public void MoveDeferredResendData(UpdatePacket<TPacketId> nextPacket) {
        foreach (var seqPacketDataPair in _deferredResendPacketData) {
            if (!nextPacket.ResendPacketData.ContainsKey(seqPacketDataPair.Key)) {
                nextPacket.ResendPacketData[seqPacketDataPair.Key] = seqPacketDataPair.Value;
            }
        }

        foreach (var seqAddonDataPair in _deferredResendAddonPacketData) {
            if (!nextPacket.ResendAddonPacketData.ContainsKey(seqAddonDataPair.Key)) {
                nextPacket.ResendAddonPacketData[seqAddonDataPair.Key] = seqAddonDataPair.Value;
            }
        }

        _deferredResendPacketData.Clear();
        _deferredResendAddonPacketData.Clear();
    }

    /// <inheritdoc />
    public override bool ReadPacket(Packet packet) {
        // TODO: maybe get rid of exception catching in packet reading and rely on bool return values for all
        // packet reading methods (including Packet class)
        try {
            ReadHeaders(packet);
        } catch (Exception e) {
            Logger.Debug($"Exception while reading headers of packet:\n{e}");
            return false;
        }

        if (!base.ReadPacket(packet)) {
            return false;
        }

        try {
            // Read the length of the resend data
            var resendLength = packet.ReadUShort();

            while (resendLength-- > 0) {
                // Read the sequence number of the packet it was lost from
                var seq = packet.ReadUShort();

                // Create a new dictionary for the packet data and read the data from the packet into it
                var packetData = new Dictionary<TPacketId, IPacketData>();
                ReadPacketData(packet, packetData);

                // Input the data into the resend dictionary keyed by its sequence number
                ResendPacketData[seq] = packetData;
            }

            // Read the length of the addon resend data
            resendLength = packet.ReadUShort();

            while (resendLength-- > 0) {
                // Read the sequence number of the packet it was lost from
                var seq = packet.ReadUShort();

                // Create a new dictionary for the addon data and read the data from the packet into it
                var addonDataDict = new Dictionary<byte, AddonPacketData>();
                ReadAddonDataDict(packet, addonDataDict);

                // Input the dictionary into the resend dictionary keyed by its sequence number
                ResendAddonPacketData[seq] = addonDataDict;
            }
        } catch (Exception e) {
            Logger.Debug($"Exception while reading update packet resend data:\n{e}");
            return false;
        }

        return true;
    }
    
    /// <summary>
    /// Set the reliable packet data contained in the lost packet as resend data in this one.
    /// </summary>
    /// <param name="lostPacket">The update packet instance that was lost.</param>
    public void SetLostReliableData(UpdatePacket<TPacketId> lostPacket) {
        // Put the reliable data that was originally sent in the lost packet in the resend dictionary keyed by its
        // sequence number
        var reliablePacketData = CopyReliableDataDict(
            lostPacket.NormalPacketData,
            t => NormalPacketData.ContainsKey(t)
        );
        if (reliablePacketData.Count > 0) {
            ResendPacketData[lostPacket.Sequence] = reliablePacketData;
        }

        // Create a new dictionary of addon data in which we store all reliable data from the lost packet
        // for all addons in the dictionary
        var toResendAddonData = new Dictionary<byte, AddonPacketData>();

        foreach (var idLostDataPair in lostPacket.AddonPacketData) {
            var addonId = idLostDataPair.Key;
            var addonPacketData = idLostDataPair.Value;

            // Construct a new AddonPacketData instance that holds the reliable data only
            var newAddonPacketData = addonPacketData.GetEmptyCopy();
            newAddonPacketData.PacketData = CopyReliableDataDict(
                addonPacketData.PacketData,
                rawPacketId =>
                    AddonPacketData.TryGetValue(addonId, out var existingAddonData)
                    && existingAddonData.PacketData.ContainsKey(rawPacketId));

            if (newAddonPacketData.PacketData.Count > 0) {
                toResendAddonData[addonId] = newAddonPacketData;
            }
        }

        // Put the addon data dictionary in the resend dictionary keyed by its sequence number
        if (toResendAddonData.Count > 0) {
            ResendAddonPacketData[lostPacket.Sequence] = toResendAddonData;
        }

        // Data that the lost packet was itself resending keeps the sequence number it was originally sent with.
        // The receiver recognizes resent data as duplicate by that sequence number, so filing it under the lost
        // packet would deliver it twice whenever the original did arrive, only its ack was late.
        foreach (var seqPacketDataPair in lostPacket.ResendPacketData) {
            if (!ResendPacketData.ContainsKey(seqPacketDataPair.Key)) {
                ResendPacketData[seqPacketDataPair.Key] = seqPacketDataPair.Value;
            }
        }

        foreach (var seqAddonDataPair in lostPacket.ResendAddonPacketData) {
            if (!ResendAddonPacketData.ContainsKey(seqAddonDataPair.Key)) {
                ResendAddonPacketData[seqAddonDataPair.Key] = seqAddonDataPair.Value;
            }
        }
    }
    
    /// <summary>
    /// Copy all reliable data in the given dictionary of lost packet data into a new dictionary.
    /// </summary>
    /// <param name="lostPacketData">The dictionary containing all packet data from a lost packet.</param>
    /// <param name="reliabilityCheck">Function that checks whether for a given key there is newer data
    /// available. If it returns true, lost data will be dropped.</param>
    /// <typeparam name="TKey">The key parameter of the dictionaries to copy.</typeparam>
    /// <returns>A new dictionary containing only the reliable data.</returns>
    private Dictionary<TKey, IPacketData> CopyReliableDataDict<TKey>(
        Dictionary<TKey, IPacketData> lostPacketData,
        Func<TKey, bool> reliabilityCheck
    ) {
        // Create a new dictionary of packet data in which we store all reliable data
        var reliablePacketData = new Dictionary<TKey, IPacketData>();

        foreach (var keyDataPair in lostPacketData) {
            var key = keyDataPair.Key;
            var data = keyDataPair.Value;

            // Check if the packet data is supposed to be reliable
            if (!data.IsReliable) {
                continue;
            }

            // Check whether we can drop it since a newer version of that data already exists
            if (data.DropReliableDataIfNewerExists && reliabilityCheck(key)) {
                continue;
            }

            // Logger.Info($"  Resending {data.GetType()} data");
            reliablePacketData[key] = data;
        }

        return reliablePacketData;
    }

    /// <inheritdoc />
    protected override void CacheAllPacketData() {
        base.CacheAllPacketData();
        
        void AddResendData<TKey>(
            Dictionary<TKey, IPacketData> dataDict,
            Dictionary<TKey, IPacketData> cachedData
        ) {
            foreach (var packetIdDataPair in dataDict) {
                // Get the ID and the data itself
                var packetId = packetIdDataPair.Key;
                var packetData = packetIdDataPair.Value;

                // Check whether for this ID there already exists data
                if (cachedData.TryGetValue(packetId, out var existingPacketData)) {
                    // If the existing data is a PacketDataCollection, we can simply add all the data instance to it
                    // If not, we simply discard the resent data, since it is older
                    if (existingPacketData is RawPacketDataCollection existingPacketDataCollection
                        && packetData is RawPacketDataCollection packetDataCollection) {
                        existingPacketDataCollection.DataInstances.AddRange(packetDataCollection.DataInstances);
                    }
                } else {
                    // If no data exists for this ID, we can simply set the resent data for that key
                    cachedData[packetId] = packetData;
                }
            }
        }
        
        // Iteratively add the resent packet data, but make sure to merge it with existing data
        foreach (var resentPacketData in ResendPacketData.Values) {
            AddResendData(resentPacketData, CachedAllPacketData);
        }
        
        // Iteratively add the resent addon data, but make sure to merge it with existing data
        foreach (var resentAddonData in ResendAddonPacketData.Values) {
            foreach (var addonIdDataPair in resentAddonData) {
                var addonId = addonIdDataPair.Key;
                var addonPacketData = addonIdDataPair.Value;

                if (CachedAllAddonData.TryGetValue(addonId, out var existingAddonPacketData)) {
                    AddResendData(addonPacketData.PacketData, existingAddonPacketData.PacketData);
                } else {
                    CachedAllAddonData[addonId] = addonPacketData;
                }
            }
        }
        
        IsAllPacketDataCached = true;
    }
    
    /// <summary>
    /// Drops resend data that is duplicate, i.e. that we already received in an earlier packet, either in the packet
    /// it was originally sent in or resent in another packet. The resend data that remains is added to the
    /// respective history.
    /// </summary>
    /// <param name="receivedSequences">The sequence numbers of packets that were already received.</param>
    /// <param name="receivedResendSequences">The original sequence numbers of resent packet data that was already
    /// received.</param>
    /// <param name="receivedAddonResendSequences">The original sequence numbers of resent addon data that was
    /// already received.</param>
    public void DropDuplicateResendData(
        SequenceHistory receivedSequences,
        SequenceHistory receivedResendSequences,
        SequenceHistory receivedAddonResendSequences
    ) {
        DropDuplicates(ResendPacketData, receivedSequences, receivedResendSequences);
        DropDuplicates(ResendAddonPacketData, receivedSequences, receivedAddonResendSequences);

        static void DropDuplicates<TData>(
            Dictionary<ushort, TData> resendData,
            SequenceHistory receivedSequences,
            SequenceHistory receivedResendSequences
        ) {
            foreach (var resendSequence in new List<ushort>(resendData.Keys)) {
                if (receivedSequences.Contains(resendSequence) || receivedResendSequences.Contains(resendSequence)) {
                    resendData.Remove(resendSequence);
                } else {
                    receivedResendSequences.Add(resendSequence);
                }
            }
        }
    }
}
