using System.Buffers.Binary;
using System.Net;

namespace Karvon.Ports.Jprq;

/// <summary>
/// The frames of a jprq tunnel's one connection to its server's event port, as tulki.uz's jprq server multiplexes it (a
/// fork of jprq; the original opened a port per tunnel): a byte of type, the stream (little-endian; 0 for the tunnel
/// itself), the payload's length, the payload. Control messages carry a gob struct; a public connection's bytes go as
/// data frames of its stream.
/// </summary>
public enum JprqMessage : byte
{
    TunnelRequested = 1,
    TunnelOpened = 2,
    ConnectionOpened = 3,
    ConnectionData = 4,
    ConnectionClose = 5,
    ConnectionLimit = 6,
    Ping = 7,
}

public readonly record struct JprqFrame(JprqMessage Type, uint Stream, byte[] Payload)
{
    const int HeaderLength = 9;

    /// <summary>The server's limit on a frame read.</summary>
    const int MaxLength = 64 * 1024 * 1024;

    /// <summary>The most a data frame carries (larger reads are split).</summary>
    public const int MaxData = 32 * 1024;

    public static async Task<JprqFrame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        if (!await FillAsync(stream, header, ct))
            return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5));
        if (length > MaxLength)
            throw new IOException($"A frame of {length} bytes.");
        var payload = length == 0 ? [] : new byte[length];
        if (length > 0 && !await FillAsync(stream, payload, ct))
            throw new EndOfStreamException("The tunnel's server closed the connection within a frame.");
        return new JprqFrame((JprqMessage)header[0], BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1)), payload);
    }

    /// <summary>The frame's bytes, header and payload.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[HeaderLength + Payload.Length];
        bytes[0] = (byte)Type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), Stream);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), (uint)Payload.Length);
        Payload.CopyTo(bytes, HeaderLength);
        return bytes;
    }

    /// <summary>All of <paramref name="buffer"/>; false when the stream ended before its first byte.</summary>
    static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                return read == 0 ? false : throw new EndOfStreamException("The tunnel's server closed the connection within a frame.");
            read += n;
        }
        return true;
    }
}

/// <summary>What the client asks for: a tunnel of a protocol ("http", "tcp"), a subdomain (empty: the key's own name), a fixed public port for TCP.</summary>
public sealed record TunnelRequest(string Protocol, string Subdomain, string AuthToken, string CliVersion, ushort PublicPort = 0, string CanonName = "")
{
    public byte[] Encode() => Gob.Encode("TunnelRequested",
    [
        ("Protocol", Gob.String, Protocol), ("Subdomain", Gob.String, Subdomain), ("CanonName", Gob.String, CanonName),
        ("AuthToken", Gob.String, AuthToken), ("CliVersion", Gob.String, CliVersion), ("PublicPort", Gob.Uint, PublicPort),
    ]);
}

/// <summary>The server's answer: the tunnel's host and public port, or why it is refused.</summary>
public sealed record TunnelOpened(string Hostname, string Protocol, ushort PublicServer, string ErrorMessage)
{
    public static TunnelOpened Decode(byte[] payload)
    {
        var fields = Gob.Decode(payload);
        return new TunnelOpened(Str(fields, "Hostname"), Str(fields, "Protocol"), (ushort)Num(fields, "PublicServer"), Str(fields, "ErrorMessage"));
    }

    internal static string Str(IReadOnlyDictionary<string, object> fields, string name) => fields.TryGetValue(name, out var v) ? (string)v : "";

    internal static ulong Num(IReadOnlyDictionary<string, object> fields, string name) => fields.TryGetValue(name, out var v) ? (ulong)v : 0;
}

/// <summary>A visitor connected to the tunnel's public address: from where.</summary>
public sealed record ConnectionOpened(IPAddress? ClientIp, ushort ClientPort)
{
    public static ConnectionOpened Decode(byte[] payload)
    {
        var fields = Gob.Decode(payload);
        return new ConnectionOpened(Ip(fields), (ushort)TunnelOpened.Num(fields, "ClientPort"));
    }

    internal static IPAddress? Ip(IReadOnlyDictionary<string, object> fields)
    {
        if (!fields.TryGetValue("ClientIP", out var v) || v is not byte[] { Length: 4 or 16 } bytes)
            return null;
        var ip = new IPAddress(bytes);
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }
}
