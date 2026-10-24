using System.Net;
using Karvon.Ports.Jprq;

namespace Karvon.Tests;

/// <summary>jprq's frames and gob messages, against what its own Go code writes (the events package of tulki.uz's server).</summary>
public class PortsProtocolTests
{
    // Frames written by events.FramedConn.SendControl (Go 1.26).
    const string Requested =
        "01000000008f000000717f0301010f54756e6e656c52657175657374656401ff80000106010850726f746f636f6c010c000109537562646f6d61696e010c00010943616e6f6e4e616d65010c00010941757468546f6b656e010c00010a436c6956657273696f6e010c00010a5075626c6963506f727401060000001cff80010468747470010464656d6f0203746f6b01066b6172766f6e00";

    const string RequestedTcp =
        "01000000008c000000717f0301010f54756e6e656c52657175657374656401ff80000106010850726f746f636f6c010c000109537562646f6d61696e010c00010943616e6f6e4e616d65010c00010941757468546f6b656e010c00010a436c6956657273696f6e010c00010a5075626c6963506f7274010600000019ff8001037463700303746f6b01066b6172766f6e01fe80e900";

    const string Opened =
        "02000000007600000056ff810301010c54756e6e656c4f70656e656401ff820001040108486f73746e616d65010c00010850726f746f636f6c010c00010c5075626c6963536572766572010600010c4572726f724d657373616765010c0000001eff82010f64656d6f2e74756c6b692e7465737401046874747001fe01bb00";

    const string OpenedError =
        "02000000008500000056ff810301010c54756e6e656c4f70656e656401ff820001040108486f73746e616d65010c00010850726f746f636f6c010c00010c5075626c6963536572766572010600010c4572726f724d657373616765010c0000002dff820428737562646f6d61696e20697320627573793a2064656d6f2c2074727920616e6f74686572206f6e6500";

    const string Connection =
        "0300000000550000003aff8303010110436f6e6e656374696f6e4f70656e656401ff840001020108436c69656e744950010a00010a436c69656e74506f7274010600000019ff84011000000000000000000000ffff0102030401fec35000";

    static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    static async Task<JprqFrame> Read(string hex) => (await JprqFrame.ReadAsync(new MemoryStream(Bytes(hex)), default))!.Value;

    [Fact]
    public void A_request_is_written_as_Go_writes_it()
    {
        var http = new TunnelRequest("http", "demo", "tok", "karvon");
        Assert.Equal(Requested, Convert.ToHexStringLower(new JprqFrame(JprqMessage.TunnelRequested, 0, http.Encode()).ToBytes()));
        var tcp = new TunnelRequest("tcp", "", "tok", "karvon", PublicPort: 33001);
        Assert.Equal(RequestedTcp, Convert.ToHexStringLower(new JprqFrame(JprqMessage.TunnelRequested, 0, tcp.Encode()).ToBytes()));
    }

    [Fact]
    public async Task The_servers_answers_are_read_by_the_definitions_they_carry()
    {
        // The server's type ids are its own (65, 66 here), not the first a new encoder gives.
        var opened = await Read(Opened);
        Assert.Equal((JprqMessage.TunnelOpened, 0u), (opened.Type, opened.Stream));
        Assert.Equal(new TunnelOpened("demo.tulki.test", "http", 443, ""), TunnelOpened.Decode(opened.Payload));
        Assert.Equal("subdomain is busy: demo, try another one", TunnelOpened.Decode((await Read(OpenedError)).Payload).ErrorMessage);
        var connection = ConnectionOpened.Decode((await Read(Connection)).Payload);
        Assert.Equal((IPAddress.Parse("1.2.3.4"), (ushort)50000), (connection.ClientIp, connection.ClientPort));

        // A request read back: what the server decodes.
        var request = Gob.Decode((await Read(Requested)).Payload);
        Assert.Equal(["AuthToken", "CliVersion", "Protocol", "Subdomain"], request.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(33001ul, Gob.Decode((await Read(RequestedTcp)).Payload)["PublicPort"]);
    }

    [Fact]
    public async Task A_frame_cut_short_is_an_error_and_an_ended_stream_none()
    {
        Assert.Null(await JprqFrame.ReadAsync(new MemoryStream(), default));
        await Assert.ThrowsAsync<EndOfStreamException>(() => JprqFrame.ReadAsync(new MemoryStream(Bytes(Opened)[..20]), default));
        Assert.Throws<FormatException>(() => Gob.Decode(Bytes(Opened)[9..40]));
    }
}
