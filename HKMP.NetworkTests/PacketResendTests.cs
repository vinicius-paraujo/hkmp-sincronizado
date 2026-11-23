using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Xunit;

namespace Hkmp.NetworkTests;

/// <summary>
/// Lost reliable data travels again inside a later packet, which may already carry data of the same kind.
/// </summary>
public class PacketResendTests {
    [Fact]
    public void Resent_data_that_meets_newer_data_of_its_kind_still_reaches_its_handler_first() {
        var read = Transmit(lost: new ChatMessage { Message = "older" }, current: new ChatMessage { Message = "newer" });

        Assert.Equal("newer", ((ChatMessage) read.GetPacketData()[ServerUpdatePacketId.ChatMessage]).Message);
        var earlier = Assert.Single(read.GetEarlierResentData());
        Assert.Equal(ServerUpdatePacketId.ChatMessage, earlier.PacketId);
        Assert.Equal("older", ((ChatMessage) earlier.Data).Message);
    }

    [Fact]
    public void Resent_data_that_a_newer_version_replaces_is_dropped() {
        var read = Transmit(
            lost: SkinSetting(1),
            current: SkinSetting(2),
            packetId: ServerUpdatePacketId.PlayerSetting
        );

        Assert.Equal(2, ((ServerPlayerSettingUpdate) read.GetPacketData()[ServerUpdatePacketId.PlayerSetting]).SkinId);
        Assert.Empty(read.GetEarlierResentData());
    }

    [Fact]
    public void Resent_data_without_newer_data_of_its_kind_takes_its_own_slot() {
        var lostPacket = new ServerUpdatePacket { Sequence = 1 };
        lostPacket.SetSendingPacketData(ServerUpdatePacketId.ChatMessage, new ChatMessage { Message = "older" });
        var current = new ServerUpdatePacket { Sequence = 2 };
        current.SetLostReliableData(lostPacket);

        var read = Reread(current);

        Assert.Equal("older", ((ChatMessage) read.GetPacketData()[ServerUpdatePacketId.ChatMessage]).Message);
        Assert.Empty(read.GetEarlierResentData());
    }

    private static ServerUpdatePacket Transmit(
        IPacketData lost,
        IPacketData current,
        ServerUpdatePacketId packetId = ServerUpdatePacketId.ChatMessage
    ) {
        var lostPacket = new ServerUpdatePacket { Sequence = 1 };
        lostPacket.SetSendingPacketData(packetId, lost);
        var currentPacket = new ServerUpdatePacket { Sequence = 2 };
        currentPacket.SetSendingPacketData(packetId, current);
        currentPacket.SetLostReliableData(lostPacket);

        return Reread(currentPacket);
    }

    private static ServerPlayerSettingUpdate SkinSetting(byte skin) {
        var setting = new ServerPlayerSettingUpdate { SkinId = skin };
        setting.UpdateTypes.Add(PlayerSettingUpdateType.Skin);
        return setting;
    }

    private static ServerUpdatePacket Reread(ServerUpdatePacket sent) {
        var raw = new Packet();
        sent.CreatePacket(raw);
        var bytes = raw.ToArray();

        // The receiving side takes the packet apart from the datagram as it arrives, length prefix included.
        var received = Assert.Single(PacketManager.HandleReceivedData(bytes, bytes.Length, new PacketFragments()));
        var read = new ServerUpdatePacket();
        Assert.True(read.ReadPacket(received), "The packet could not be read back.");
        return read;
    }
}
