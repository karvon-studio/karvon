using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Karvon.Postman;

/// <summary>AWS's signature of a request (version 4), in its headers or its query.</summary>
static class PostmanAws
{
    public sealed record Keys(string AccessKey, string SecretKey, string SessionToken, string Region, string Service);

    /// <summary>The keys of a sign-in, its region and service found in the host when they are not given.</summary>
    public static Keys Of(Func<string, string> value, Uri uri)
    {
        var labels = uri.Host.Split('.');
        var aws = uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase);
        // id.execute-api.eu-west-1.amazonaws.com, dynamodb.eu-west-1.amazonaws.com, s3.amazonaws.com
        var at = aws && labels.Length >= 5 ? labels.Length - 4 : 0;
        var service = value("service") is { Length: > 0 } given ? given : aws ? labels[at] : "execute-api";
        var region = value("region") is { Length: > 0 } where ? where : aws && labels.Length >= 4 ? labels[^3] : "us-east-1";
        return new Keys(value("accessKey"), value("secretKey"), value("sessionToken"), region, service);
    }

    /// <summary>The headers signed (put in <paramref name="headers"/>), or the URL with the signature in its query.</summary>
    public static string Sign(Keys keys, string method, string url, List<(string Name, string Value)> headers, byte[] body, bool inQuery, DateTimeOffset now)
    {
        var (time, day) = (now.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture), now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        var scope = $"{day}/{keys.Region}/{keys.Service}/aws4_request";
        var isS3 = keys.Service == "s3";
        var hash = inQuery && isS3 ? "UNSIGNED-PAYLOAD" : Convert.ToHexStringLower(SHA256.HashData(body));
        if (inQuery)
        {
            var rows = new List<PostmanPair>(PostmanRequest.QueryOf(url))
            {
                new("X-Amz-Algorithm", "AWS4-HMAC-SHA256"), new("X-Amz-Credential", Uri.EscapeDataString($"{keys.AccessKey}/{scope}")), new("X-Amz-Date", time),
                new("X-Amz-Expires", "86400"),
            };
            if (keys.SessionToken.Length > 0)
                rows.Add(new("X-Amz-Security-Token", Uri.EscapeDataString(keys.SessionToken)));
            rows.Add(new("X-Amz-SignedHeaders", "host"));
            url = PostmanRequest.WithQuery(url, rows);
            var signature = Signature(keys, method, new Uri(url), [("host", new Uri(url).Authority)], hash, time, day, scope, isS3);
            return PostmanRequest.WithQuery(url, rows.Append(new PostmanPair("X-Amz-Signature", signature)));
        }
        var uri = new Uri(url);
        var signed = new List<(string Name, string Value)> { ("host", uri.Authority), ("x-amz-date", time) };
        if (isS3)
            signed.Add(("x-amz-content-sha256", hash));
        if (keys.SessionToken.Length > 0)
            signed.Add(("x-amz-security-token", keys.SessionToken));
        foreach (var (name, value) in signed.Skip(1))
        {
            headers.RemoveAll(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            headers.Add((string.Join('-', name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..])), value));
        }
        signed.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        headers.Add(("Authorization", $"AWS4-HMAC-SHA256 Credential={keys.AccessKey}/{scope}, SignedHeaders={string.Join(';', signed.Select(h => h.Name))}, "
            + $"Signature={Signature(keys, method, uri, signed, hash, time, day, scope, isS3)}"));
        return url;
    }

    static string Signature(Keys keys, string method, Uri uri, IReadOnlyList<(string Name, string Value)> signed, string hash, string time, string day, string scope, bool isS3)
    {
        // Every service but S3 has the path's segments escaped twice.
        var path = uri.AbsolutePath.Length == 0 ? "/" : isS3 ? uri.AbsolutePath : string.Join('/', uri.AbsolutePath.Split('/').Select(Uri.EscapeDataString));
        var query = string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .Select(p => (Key: Uri.EscapeDataString(Uri.UnescapeDataString(p[0])), Value: Uri.EscapeDataString(Uri.UnescapeDataString(p.ElementAtOrDefault(1) ?? ""))))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        var canonical = $"{method}\n{path}\n{query}\n{string.Concat(signed.Select(h => $"{h.Name}:{h.Value.Trim()}\n"))}\n{string.Join(';', signed.Select(h => h.Name))}\n{hash}";
        var text = $"AWS4-HMAC-SHA256\n{time}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Encoding.UTF8.GetBytes("AWS4" + keys.SecretKey);
        foreach (var part in new[] { day, keys.Region, keys.Service, "aws4_request" })
            key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>HTTP's digest sign-in (RFC 7616): the server's challenge answered.</summary>
static class PostmanDigest
{
    /// <summary>A challenge's parts; null when the headers have none.</summary>
    public static Dictionary<string, string>? Challenge(IEnumerable<string> headers)
    {
        // The strongest it can answer, of those a server offers.
        Dictionary<string, string>? found = null;
        foreach (var header in headers)
            if (header.TrimStart().StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(header.TrimStart()[7..], "(\\w+)\\s*=\\s*(?:\"((?:[^\"\\\\]|\\\\.)*)\"|([^,\\s]+))"))
                    parts[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : m.Groups[3].Value;
                if (found is null || (Hash(parts.GetValueOrDefault("algorithm")) == HashAlgorithmName.SHA256 && Hash(found.GetValueOrDefault("algorithm")) != HashAlgorithmName.SHA256))
                    found = parts;
            }
        return found;
    }

    static HashAlgorithmName? Hash(string? algorithm) => (algorithm ?? "MD5").ToUpperInvariant() switch
    {
        "" or "MD5" or "MD5-SESS" => HashAlgorithmName.MD5,
        "SHA-256" or "SHA-256-SESS" => HashAlgorithmName.SHA256,
        _ => null,
    };

    public static string Header(string username, string password, string method, string target, IReadOnlyDictionary<string, string> challenge, byte[] body, string? cnonce = null, int count = 1)
    {
        var algorithm = challenge.GetValueOrDefault("algorithm") is { Length: > 0 } named ? named : "MD5";
        if (Hash(algorithm) is not { } name)
            throw new PostmanException($"Its digest sign-in's algorithm ({algorithm}) is not one Karvon answers: MD5 and SHA-256 are.");
        string H(byte[] data) => Convert.ToHexStringLower(CryptographicOperations.HashData(name, data));
        string T(string text) => H(Encoding.UTF8.GetBytes(text));
        var (realm, nonce) = (challenge.GetValueOrDefault("realm", ""), challenge.GetValueOrDefault("nonce", ""));
        var offered = challenge.GetValueOrDefault("qop", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var qop = offered.Contains("auth", StringComparer.OrdinalIgnoreCase) ? "auth" : offered.Contains("auth-int", StringComparer.OrdinalIgnoreCase) ? "auth-int" : null;
        cnonce ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var nc = count.ToString("x8", CultureInfo.InvariantCulture);
        var ha1 = T($"{username}:{realm}:{password}");
        if (algorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase))
            ha1 = T($"{ha1}:{nonce}:{cnonce}");
        var ha2 = T(qop == "auth-int" ? $"{method}:{target}:{H(body)}" : $"{method}:{target}");
        var response = T(qop is null ? $"{ha1}:{nonce}:{ha2}" : $"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
        static string Q(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return $"Digest username={Q(username)}, realm={Q(realm)}, nonce={Q(nonce)}, uri={Q(target)}, algorithm={algorithm}, response=\"{response}\""
            + (qop is null ? "" : $", qop={qop}, nc={nc}, cnonce={Q(cnonce)}")
            + (challenge.GetValueOrDefault("opaque") is { Length: > 0 } opaque ? $", opaque={Q(opaque)}" : "");
    }
}

/// <summary>Windows' NTLM sign-in (NTLMv2): its three messages, on one connection.</summary>
static class PostmanNtlm
{
    const uint Flags = 0x1 | 0x4 | 0x200 | 0x8000 | 0x80000 | 0x20000000 | 0x80000000;

    static readonly byte[] Signature = "NTLMSSP\0"u8.ToArray();

    /// <summary>The first message: what is offered.</summary>
    public static string Negotiate()
    {
        var message = new byte[32];
        Signature.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), Flags);
        return "NTLM " + Convert.ToBase64String(message);
    }

    /// <summary>The server's second message, of its headers; null when there is none.</summary>
    public static byte[]? Challenge(IEnumerable<string> headers)
    {
        foreach (var header in headers)
            if (header.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase))
                try
                {
                    if (Convert.FromBase64String(header[5..].Trim()) is { Length: >= 32 } message && message.AsSpan(0, 8).SequenceEqual(Signature))
                        return message;
                }
                catch (FormatException)
                {
                }
        return null;
    }

    public static byte[] OwfV2(string username, string password, string domain) =>
        HMACMD5.HashData(Md4(Encoding.Unicode.GetBytes(password)), Encoding.Unicode.GetBytes(username.ToUpperInvariant() + domain));

    /// <summary>The proof and what it is of: the third message's answer to the challenge.</summary>
    public static byte[] Response(byte[] owf, byte[] serverChallenge, byte[] clientChallenge, long time, byte[] targetInfo)
    {
        var blob = new byte[28 + targetInfo.Length + 4];
        blob[0] = blob[1] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(8), time);
        clientChallenge.CopyTo(blob, 16);
        targetInfo.CopyTo(blob, 28);
        byte[] signed = [.. serverChallenge, .. blob];
        return [.. HMACMD5.HashData(owf, signed), .. blob];
    }

    /// <summary>The third message: who signs in, and the proof.</summary>
    public static string Authenticate(byte[] challenge, string username, string password, string domain, string workstation)
    {
        // DOMAIN\user, as Windows writes it.
        if (domain.Length == 0 && username.Split('\\') is [var before, var after])
            (domain, username) = (before, after);
        var serverChallenge = challenge[24..32];
        byte[] info = [];
        if (challenge.Length >= 48 && BinaryPrimitives.ReadUInt16LittleEndian(challenge.AsSpan(40)) is var length && BinaryPrimitives.ReadInt32LittleEndian(challenge.AsSpan(44)) is var offset
            && offset >= 0 && offset + length <= challenge.Length)
            info = challenge[offset..(offset + length)];
        var time = DateTimeOffset.UtcNow.ToFileTime();
        var timed = false;
        for (var at = 0; at + 4 <= info.Length;)
        {
            var (id, size) = (BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(at)), BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(at + 2)));
            if (id == 0 || at + 4 + size > info.Length)
                break;
            if (id == 7 && size == 8)
                (time, timed) = (BinaryPrimitives.ReadInt64LittleEndian(info.AsSpan(at + 4)), true);
            at += 4 + size;
        }
        var owf = OwfV2(username, password, domain);
        var client = RandomNumberGenerator.GetBytes(8);
        var nt = Response(owf, serverChallenge, client, time, info);
        // With the server's time there is no LM answer.
        byte[] both = [.. serverChallenge, .. client];
        byte[] lm = timed ? new byte[24] : [.. HMACMD5.HashData(owf, both), .. client];
        var parts = new[] { lm, nt, Encoding.Unicode.GetBytes(domain), Encoding.Unicode.GetBytes(username), Encoding.Unicode.GetBytes(workstation), [] };
        var header = new byte[64];
        Signature.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 3);
        var from = 64;
        for (var i = 0; i < parts.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12 + i * 8), (ushort)parts[i].Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14 + i * 8), (ushort)parts[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16 + i * 8), from);
            from += parts[i].Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), Flags & BinaryPrimitives.ReadUInt32LittleEndian(challenge.AsSpan(20)));
        return "NTLM " + Convert.ToBase64String([.. header, .. parts.SelectMany(p => p)]);
    }

    /// <summary>MD4 (RFC 1320), which NTLM's password hash is and .NET has no more.</summary>
    public static byte[] Md4(byte[] data)
    {
        var padded = new byte[(data.Length + 8) / 64 * 64 + 64];
        data.CopyTo(padded, 0);
        padded[data.Length] = 0x80;
        BinaryPrimitives.WriteInt64LittleEndian(padded.AsSpan(padded.Length - 8), (long)data.Length * 8);
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;
        var x = new uint[16];
        for (var block = 0; block < padded.Length; block += 64)
        {
            for (var i = 0; i < 16; i++)
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(block + i * 4));
            var (aa, bb, cc, dd) = (a, b, c, d);
            void Step(uint f, uint word, uint constant, int shift)
            {
                var next = BitOperations.RotateLeft(a + f + word + constant, shift);
                (a, d, c, b) = (d, c, b, next);
            }
            int[] s1 = [3, 7, 11, 19], s2 = [3, 5, 9, 13], s3 = [3, 9, 11, 15];
            int[] k3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
            for (var i = 0; i < 16; i++)
                Step((b & c) | (~b & d), x[i], 0, s1[i % 4]);
            for (var i = 0; i < 16; i++)
                Step((b & c) | (b & d) | (c & d), x[i / 4 + i % 4 * 4], 0x5a827999, s2[i % 4]);
            for (var i = 0; i < 16; i++)
                Step(b ^ c ^ d, x[k3[i]], 0x6ed9eba1, s3[i % 4]);
            (a, b, c, d) = (a + aa, b + bb, c + cc, d + dd);
        }
        var hash = new byte[16];
        foreach (var (value, i) in new[] { a, b, c, d }.Select((v, i) => (v, i)))
            BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(i * 4), value);
        return hash;
    }
}
