using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Postman;
using Karvon.Postman.ViewModels;
using Karvon.Services;
using static Karvon.Tests.PostmanTests;

namespace Karvon.Tests;

/// <summary>What Postman's scripts require, the sign-ins that answer a server, cookies, OAuth 2.0's tokens, a run's data.</summary>
public class PostmanSandboxTests
{
    // ---- require ----------------------------------------------------------------------------------------------

    static void Passes(string script)
    {
        var (tests, _, error, _) = Script(script);
        Assert.Null(error);
        Assert.NotEmpty(tests);
        Assert.All(tests, t => Assert.True(t.Passed, $"{t.Name}: {t.Error}"));
    }

    [Fact]
    public void Scripts_have_CryptoJS() => Passes("""
        const CJ = require('crypto-js');
        pm.test('hashes', () => {
          pm.expect(CryptoJS.MD5('abc').toString()).to.equal('900150983cd24fb0d6963f7d28e17f72');
          pm.expect(CJ.SHA1('abc').toString()).to.equal('a9993e364706816aba3e25717850c26c9cd0d89d');
          pm.expect(CJ.SHA256('abc').toString(CJ.enc.Hex)).to.equal('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
          pm.expect(CJ.SHA512('abc').toString().slice(0, 16)).to.equal('ddaf35a193617aba');
        });
        pm.test('hmac', () => pm.expect(CJ.HmacSHA256('The quick brown fox jumps over the lazy dog', 'key').toString(CJ.enc.Base64)).to.equal('97yD9DBThCSxMpjmqm+xQ+9NWaFJRhdZl0edvC0aPNg='));
        pm.test('aes', () => {
          const key = CJ.enc.Hex.parse('2b7e151628aed2a6abf7158809cf4f3c'), iv = CJ.enc.Hex.parse('000102030405060708090a0b0c0d0e0f');
          const c = CJ.AES.encrypt(CJ.enc.Hex.parse('6bc1bee22e409f96e93d7e117393172a'), key, { iv, padding: CJ.pad.NoPadding });
          pm.expect(c.ciphertext.toString()).to.equal('7649abac8119b246cee98e9b12e9197d');
          pm.expect(CJ.AES.decrypt(CJ.AES.encrypt('salom', 'parol').toString(), 'parol').toString(CJ.enc.Utf8)).to.equal('salom');
        });
        pm.test('pbkdf2', () => pm.expect(CJ.PBKDF2('password', 'salt', { keySize: 5, iterations: 1, hasher: CJ.algo.SHA1 }).toString()).to.equal('0c60c80f961f0e71f3a9b524af6012062fe037a6'));
        """);

    [Fact]
    public void Scripts_have_lodash_moment_ajv_tv4_and_cheerio() => Passes("""
        pm.test('lodash', () => { const _ = require('lodash'); pm.expect(_.get({ a: [{ b: 7 }] }, 'a[0].b')).to.equal(7); pm.expect(_.groupBy([1, 2, 3], n => n % 2)).to.eql({ 0: [2], 1: [1, 3] }); pm.expect(_.camelCase('a-b c')).to.equal('aBC'); pm.expect(_.template('hi <%= n %>')({ n: 'x' })).to.equal('hi x'); });
        pm.test('lodash global', () => pm.expect(_.uniq([1, 1, 2])).to.eql([1, 2]));
        pm.test('moment', () => { const m = require('moment'); pm.expect(m.utc('2026-11-25T10:00:00Z').add(1, 'day').format('YYYY-MM-DD')).to.equal('2026-11-26'); pm.expect(moment.duration(90, 'minutes').asHours()).to.equal(1.5); });
        const schema = { type: 'object', required: ['id', 'user'], properties: { id: { type: 'integer', minimum: 1 }, user: { type: 'object', properties: { name: { type: 'string', pattern: '^a' } } } } };
        pm.test('ajv', () => { const Ajv = require('ajv'), v = new Ajv().compile(schema); pm.expect(v(pm.response.json())).to.be.true; pm.expect(v({ id: 0 })).to.be.false; });
        pm.test('jsonSchema', () => { pm.response.to.have.jsonSchema(schema); pm.expect({ id: 'x' }).to.not.have.jsonSchema(schema); });
        pm.test('jsonSchema fails', () => { let m = ''; try { pm.expect({ id: 0, user: {} }).to.have.jsonSchema(schema); } catch (e) { m = e.message; } pm.expect(m).to.include('data.id should be >= 1'); });
        pm.test('tv4', () => { pm.expect(tv4.validate({ a: 1 }, { type: 'object', properties: { a: { type: 'string' } } })).to.be.false; pm.expect(require('tv4').error.message).to.include('string'); });
        pm.test('cheerio', () => {
          const $ = cheerio.load('<html><body><form id="f"><input type="hidden" name="_csrf" value="t&amp;1"><a class="x y" href="/a">one</a><a href="/b">two</a></form></body></html>');
          pm.expect($('input[name=_csrf]').val()).to.equal('t&1'); pm.expect($('a').length).to.equal(2); pm.expect($('a.x').text()).to.equal('one'); pm.expect($('#f > a:nth-child(3)').attr('href')).to.equal('/b');
          pm.expect($('a').map((i, e) => $(e).attr('href')).get().join()).to.equal('/a,/b'); $('a.x').addClass('z').text('uno'); pm.expect($.html('a.x')).to.include('class="x y z"');
        });
        """);

    [Fact]
    public void Scripts_have_the_rest_of_Postmans_list_and_Nodes() => Passes(""""
        pm.test('uuid', () => { const u = require('uuid'); pm.expect(u.v4()).to.match(/^[0-9a-f]{8}-[0-9a-f]{4}-4/); pm.expect(u()).to.not.equal(u()); pm.expect(u.validate(u.v1()) && u.validate(u.v7())).to.be.true; pm.expect(u.version(u.v7())).to.equal(7); });
        pm.test('xml2js', () => {
          const x = require('xml2js'); let got;
          x.parseString('<?xml version="1.0"?><!-- c --><r a="1"><i>one</i><i n="2">two &amp; 3</i><e/><c><![CDATA[<b>]]></c></r>', (e, v) => got = v);
          pm.expect(got).to.eql({ r: { $: { a: '1' }, i: ['one', { _: 'two & 3', $: { n: '2' } }], e: [''], c: ['<b>'] } });
          pm.expect(xml2Json('<r><i>1</i><i>2</i><o> x </o></r>')).to.eql({ r: { i: ['1', '2'], o: 'x' } });
          pm.expect(() => x.parseString('<r><i></r>')).to.throw('Unexpected close tag');
        });
        pm.test('xml2js promise', async () => { const v = await require('xml2js').parseStringPromise('<a b="c"/>', { mergeAttrs: true, explicitArray: false }); pm.expect(v).to.eql({ a: { b: 'c' } }); });
        pm.test('csv', () => { const p = require('csv-parse/lib/sync'); pm.expect(p('a,b\n1,"x, ""y"""\n2,z\n', { columns: true })).to.eql([{ a: '1', b: 'x, "y"' }, { a: '2', b: 'z' }]); pm.expect(p('a;b', { delimiter: ';' })).to.eql([['a', 'b']]); pm.expect(() => p('a,b\n1')).to.throw('inconsistent'); });
        pm.test('buffer', () => { const b = Buffer.from('salom, дунё'); pm.expect(b.toString('base64')).to.equal('c2Fsb20sINC00YPQvdGR'); pm.expect(Buffer.from('c2Fsb20sINC00YPQvdGR', 'base64').toString()).to.equal('salom, дунё'); pm.expect(require('buffer').Buffer.from('ff00', 'hex').length).to.equal(2); pm.expect(Buffer.concat([Buffer.from('a'), Buffer.from('b')]).toString('hex')).to.equal('6162'); });
        pm.test('url', () => {
          const u = new URL('https://u:p@Example.com:8443/a/../b?x=1&y=a%20b#h'); pm.expect([u.host, u.pathname, u.searchParams.get('y'), u.origin, u.hash].join(' ')).to.equal('example.com:8443 /b a b https://example.com:8443 #h');
          u.searchParams.set('z', '3'); pm.expect(u.href).to.include('z=3'); pm.expect(new URL('../c', 'http://x/a/b/').href).to.equal('http://x/a/c'); pm.expect(require('url').parse('http://h:1/p?q=2', true).query.q).to.equal('2'); pm.expect(() => new URL('nope')).to.throw('Invalid URL');
          pm.expect(new URLSearchParams({ a: '1 2', b: '&' }).toString()).to.equal('a=1+2&b=%26');
        });
        pm.test('node', () => {
          const path = require('path'), qs = require('querystring'), util = require('util'), assert = require('assert'), Emitter = require('events'), punycode = require('punycode'), { StringDecoder } = require('string_decoder');
          pm.expect(path.join('/a', '../b', 'c.txt')).to.equal('/b/c.txt'); pm.expect(path.extname('x/y.tar.gz')).to.equal('.gz'); pm.expect(path.relative('/a/b', '/a/c/d')).to.equal('../c/d');
          pm.expect(qs.stringify({ a: [1, 2], b: 'x y' })).to.equal('a=1&a=2&b=x%20y'); pm.expect(qs.parse('a=1&a=2&b=x+y')).to.eql({ a: ['1', '2'], b: 'x y' });
          pm.expect(util.format('%s is %d %j', 'n', 5, { a: 1 })).to.equal('n is 5 {"a":1}'); assert.deepStrictEqual({ a: [1] }, { a: [1] }); pm.expect(() => assert.strictEqual(1, 2, 'no')).to.throw('no');
          const e = new Emitter(); let n = 0; e.once('x', v => n += v); e.emit('x', 2); e.emit('x', 2); pm.expect(n).to.equal(2);
          pm.expect(punycode.toASCII('münchen.de')).to.equal('xn--mnchen-3ya.de'); pm.expect(punycode.toUnicode('xn--mnchen-3ya.de')).to.equal('münchen.de');
          const d = new StringDecoder('utf8'), b = Buffer.from('я'); pm.expect(d.write(b.slice(0, 1)) + d.write(b.slice(1))).to.equal('я');
          require('timers').setTimeout(() => pm.test('timer', () => {}), 1); pm.expect(require('atob')(require('btoa')('x'))).to.equal('x'); pm.expect(require('chai').expect(1)).to.exist;
        });
        pm.test('crypto', () => { const a = crypto.getRandomValues(new Uint32Array(4)); pm.expect(Array.from(a).some(x => x > 0)).to.be.true; });
        pm.test('what there is not', () => { for (const m of ['fs', 'http', 'child_process', 'net', 'postman-collection', 'constructor', '../pm.js']) pm.expect(() => require(m), m).to.throw(); });
        """");

    [Fact]
    public void The_libraries_reach_nothing_of_the_machine()
    {
        foreach (var script in new[] { "process.env.HOME", "require('util').constructor.constructor('return process')().env.x", "Function('return System.IO')()", "require('lodash').template('<%= System.IO.File %>')()", "require('buffer').constructor('return importNamespace')()('System')" })
            Assert.NotNull(Script(script).Error);
    }

    // ---- Sign-ins ---------------------------------------------------------------------------------------------

    [Fact]
    public void AWS_signs_as_AWS_says()
    {
        // AWS's own examples (aws-sig-v4-test-suite): get-vanilla, get-vanilla-query-order-key-case, post-x-www-form-urlencoded's kin.
        var keys = new PostmanAws.Keys("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "", "us-east-1", "service");
        var at = new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);
        string Signed(string url)
        {
            var headers = new List<(string Name, string Value)>();
            PostmanAws.Sign(keys, "GET", url, headers, [], inQuery: false, at);
            Assert.Contains(("X-Amz-Date", "20150830T123600Z"), headers);
            return headers.Single(h => h.Name == "Authorization").Value;
        }
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", Signed("https://example.amazonaws.com/"));
        Assert.EndsWith("Signature=b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500", Signed("https://example.amazonaws.com/?Param2=value2&Param1=value1"));
        // Its region and service from the host; a session's token signed; in the query.
        var found = PostmanAws.Of(n => "", new Uri("https://abc.execute-api.eu-west-1.amazonaws.com/prod"));
        Assert.Equal(("eu-west-1", "execute-api"), (found.Region, found.Service));
        Assert.Equal(("ap-south-1", "dynamodb"), (PostmanAws.Of(n => "", new Uri("https://dynamodb.ap-south-1.amazonaws.com")).Region, PostmanAws.Of(n => "", new Uri("https://dynamodb.ap-south-1.amazonaws.com")).Service));
        var session = new List<(string Name, string Value)>();
        PostmanAws.Sign(keys with { SessionToken = "st" }, "PUT", "https://bucket.s3.amazonaws.com/a%20b", session, "x"u8.ToArray(), inQuery: false, at);
        Assert.Contains(("X-Amz-Security-Token", "st"), session);
        Assert.Contains("SignedHeaders=host;x-amz-date;x-amz-security-token", session.Single(h => h.Name == "Authorization").Value);
        var query = PostmanAws.Sign(keys, "GET", "https://example.amazonaws.com/?a=1", [], [], inQuery: true, at);
        Assert.Contains("X-Amz-Credential=AKIDEXAMPLE%2F20150830%2Fus-east-1%2Fservice%2Faws4_request", query);
        Assert.Matches("&X-Amz-Signature=[0-9a-f]{64}$", query);
    }

    [Fact]
    public void A_digest_and_NTLM_answer_as_their_standards_say()
    {
        // RFC 2617's example; the same with SHA-256, worked out apart.
        var old = PostmanDigest.Challenge(["Basic realm=\"x\"", "Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\""])!;
        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", PostmanDigest.Header("Mufasa", "Circle Of Life", "GET", "/dir/index.html", old, [], "0a4f113b"));
        const string Common = "realm=\"http-auth@example.org\", qop=\"auth, auth-int\", nonce=\"7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFsH4GiTo0v\", opaque=\"FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS\"";
        // Both offered: the stronger one.
        var both = PostmanDigest.Challenge([$"Digest {Common}, algorithm=MD5", $"Digest {Common}, algorithm=SHA-256"])!;
        var header = PostmanDigest.Header("Mufasa", "Circle of Life", "GET", "/dir/index.html", both, [], "f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ");
        Assert.Contains("algorithm=SHA-256, response=\"ae3275f5f6e76c28d0ed4fb51900c638715d29f07e2bb2efb56600170ede0143\"", header);
        Assert.Contains("response=\"4ac7de2cd8b0ed600475df4bdd5a7173\"", PostmanDigest.Header("Mufasa", "Circle of Life", "GET", "/dir/index.html", PostmanDigest.Challenge([$"Digest {Common}, algorithm=MD5"])!, [], "f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ"));
        Assert.Contains("SHA-512", Assert.Throws<PostmanException>(() => PostmanDigest.Header("a", "b", "GET", "/", new Dictionary<string, string> { ["algorithm"] = "SHA-512-256" }, [])).Message);

        // MD4 (RFC 1320), and MS-NLMP 4.2.4's NTLMv2.
        Assert.Equal("31d6cfe0d16ae931b73c59d7e0c089c0", Convert.ToHexStringLower(PostmanNtlm.Md4([])));
        Assert.Equal("a448017aaf21d8525fc10ae87aa6729d", Convert.ToHexStringLower(PostmanNtlm.Md4("abc"u8.ToArray())));
        Assert.Equal("e33b4ddc9c38f2199c3e7b164fcc0536", Convert.ToHexStringLower(PostmanNtlm.Md4("12345678901234567890123456789012345678901234567890123456789012345678901234567890"u8.ToArray())));
        var owf = PostmanNtlm.OwfV2("User", "Password", "Domain");
        Assert.Equal("0c868a403bfd7a93a3001ef22ef02e3f", Convert.ToHexStringLower(owf));
        var info = Convert.FromHexString("02000c0044006f006d00610069006e0001000c005300650072007600650072000000" + "0000");
        Assert.Equal("68cd0ab851e51c96aabc927bebef6a1c", Convert.ToHexStringLower(PostmanNtlm.Response(owf, Convert.FromHexString("0123456789abcdef"), Convert.FromHexString("aaaaaaaaaaaaaaaa"), 0, info))[..32]);
    }

    static PostmanItem Item(PostmanRequest request, out PostmanCollection collection)
    {
        collection = PostmanCollection.Create(Path.Combine(Folder(), "c.postman_collection.json"), "C");
        var item = collection.Add(null, "R", isFolder: false);
        request.Write(item.Node);
        return item;
    }

    [AvaloniaFact]
    public async Task A_digest_and_NTLM_sign_in_to_a_server()
    {
        using var digest = new PostmanServer(seen =>
        {
            const string Asks = "Digest realm=\"r\", qop=\"auth\", nonce=\"n1\", opaque=\"o\", algorithm=SHA-256";
            if (!seen.Headers.TryGetValue("Authorization", out var given))
                return new PostmanServer.Answer("{}", 401, Headers: [("WWW-Authenticate", Asks)]);
            var parts = PostmanDigest.Challenge([given])!;
            var wanted = PostmanDigest.Header("ada", "pa\"ss", seen.Method, seen.Path, PostmanDigest.Challenge([Asks])!, [], parts["cnonce"]);
            return new PostmanServer.Answer("{\"ok\":" + (wanted == given ? "true" : "false") + "}");
        });
        var signIn = new Dictionary<string, string> { ["username"] = "ada", ["password"] = "pa\"ss" };
        var item = Item(new PostmanRequest { Method = "POST", Url = digest.Url + "/a?b=1", Auth = new PostmanAuth(PostmanAuth.Digest, signIn), Body = new PostmanBody(PostmanBody.RawMode) { Raw = "{}" } }, out _);
        var result = await new PostmanSender(null, null).SendAsync(item);
        Assert.Equal(("{\"ok\":true}", 2, "{}"), (result.Exchange.RawBody, digest.Requests.Count, digest.Requests[1].Body));
        // No second try when it is not wanted.
        Assert.Equal(401, (await new PostmanSender(null, null).SendAsync(item, item.Read() with { Auth = new PostmanAuth(PostmanAuth.Digest, new Dictionary<string, string>(signIn) { ["disableRetryRequest"] = "true" }) })).Exchange.Status);

        // NTLM: its messages on one connection, the proof checked as a server checks it.
        byte[] challenge = [.. "NTLMSSP\0"u8, 2, 0, 0, 0, 0, 0, 0, 0, 48, 0, 0, 0, 0x05, 0x82, 0x08, 0xa0, 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0, 0, 0, 0, 0, 8, 0, 8, 0, 48, 0, 0, 0, 2, 0, 4, 0, (byte)'D', 0, (byte)'M', 0];
        var connections = new HashSet<string>();
        using var ntlm = new PostmanServer(seen =>
        {
            if (!seen.Headers.TryGetValue("Authorization", out var given) || !given.StartsWith("NTLM ", StringComparison.Ordinal))
                return new PostmanServer.Answer("", 401, Headers: [("WWW-Authenticate", "NTLM")]);
            connections.Add(seen.From.ToString());
            var message = Convert.FromBase64String(given[5..]);
            if (message[8] == 1)
                return new PostmanServer.Answer("", 401, Headers: [("WWW-Authenticate", "NTLM " + Convert.ToBase64String(challenge))]);
            byte[] Part(int at) => message[BitConverter.ToInt32(message, at + 4)..(BitConverter.ToInt32(message, at + 4) + BitConverter.ToUInt16(message, at))];
            var (nt, domain, user) = (Part(20), Encoding.Unicode.GetString(Part(28)), Encoding.Unicode.GetString(Part(36)));
            var proof = System.Security.Cryptography.HMACMD5.HashData(PostmanNtlm.OwfV2(user, "Pa55", domain), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.Concat(nt[16..]).ToArray());
            return new PostmanServer.Answer(JsonSerializer.Serialize(new { ok = proof.SequenceEqual(nt[..16]), user, domain, seen.Body }));
        });
        var windows = await new PostmanSender(null, null).SendAsync(item, new PostmanRequest
        {
            Method = "POST", Url = ntlm.Url + "/w", Auth = new PostmanAuth(PostmanAuth.Ntlm, new Dictionary<string, string> { ["username"] = @"CORP\ada", ["password"] = "Pa55" }), Body = new PostmanBody(PostmanBody.RawMode) { Raw = "hi" },
        });
        Assert.Equal("""{"ok":true,"user":"ada","domain":"CORP","Body":"hi"}""", windows.Exchange.RawBody);
        Assert.Single(connections);
    }

    // ---- Cookies ----------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Cookies_are_kept_sent_back_and_read_by_scripts()
    {
        using var server = new PostmanServer(seen => seen.Path switch
        {
            "/login" => new PostmanServer.Answer("{}", Headers: [("Set-Cookie", "sid=Session-Value-1234567890; Path=/; HttpOnly"), ("Set-Cookie", "gone=1; Max-Age=0"), ("Set-Cookie", "evil=1; Domain=example.com")]),
            "/away" => new PostmanServer.Answer("", 302, Headers: [("Location", "/cookies"), ("Set-Cookie", "hop=2; Path=/")]),
            _ => new PostmanServer.Answer(JsonSerializer.Serialize(new { cookie = seen.Headers.GetValueOrDefault("Cookie") })),
        });
        var store = Sealed(out var vault);
        var cookies = new PostmanCookies(store);
        var item = Item(new PostmanRequest { Url = server.Url + "/login" }, out _);
        var sender = new PostmanSender(null, null) { Cookies = cookies };
        Assert.True((await sender.SendAsync(item)).Succeeded);
        Assert.Equal(["sid"], cookies.All.Select(c => c.Name));
        var sent = await sender.SendAsync(item, new PostmanRequest
        {
            Url = server.Url + "/cookies", Headers = [new("Cookie", "own=3")],
            Tests = """
                pm.test('cookies', () => { pm.expect(pm.cookies.get('sid')).to.equal('Session-Value-1234567890'); pm.expect(pm.cookies.has('nope')).to.be.false; pm.expect(pm.cookies.toObject()).to.have.property('sid'); });
                const jar = pm.cookies.jar(), url = pm.request.url.toString();
                jar.set(url, 'mine', 'm1', e => pm.test('set', () => pm.expect(e).to.be.null));
                jar.get(url, 'mine', (e, v) => pm.test('got', () => pm.expect(v).to.equal('m1')));
                pm.test('all', async () => pm.expect((await jar.getAll(url)).map(c => c.name)).to.include.members(['sid', 'mine']));
                jar.unset(url, 'sid', () => {});
                jar.getAll('http://other.example/', e => pm.test('not another host', () => pm.expect(e.message).to.include('not this script')));
                """,
        });
        Assert.True(sent.Succeeded, string.Join('\n', sent.Tests.Where(t => t.Failed).Select(t => t.Name + t.Error)) + sent.ScriptError);
        Assert.Equal("own=3; sid=Session-Value-1234567890", JsonNode.Parse(sent.Exchange.RawBody)!["cookie"]!.GetValue<string>());
        Assert.Equal(["mine"], cookies.All.Select(c => c.Name));
        // On the way of a redirect; none where a request says so, and not followed where it says that.
        Assert.Contains("hop=2", (await sender.SendAsync(item, new PostmanRequest { Url = server.Url + "/away" })).Exchange.RawBody);
        item.Node["protocolProfileBehavior"] = new JsonObject { ["disableCookies"] = true, ["followRedirects"] = false };
        Assert.Equal(302, (await sender.SendAsync(item, new PostmanRequest { Url = server.Url + "/away" })).Exchange.Status);
        item.Node.Remove("protocolProfileBehavior");
        // Kept sealed, and there the next time; an agent has its own.
        await store.Written;
        Assert.Contains("mine", vault.GetSealed("postman/cookies"));
        Assert.Contains("mine", new PostmanCookies(store).All.Select(c => c.Name));
        var agent = new PostmanAgentLayer();
        var agents = await new PostmanSender(null, null) { Agent = agent, Cookies = cookies }.SendAsync(item, new PostmanRequest { Url = server.Url + "/cookies", Tests = "console.log(JSON.stringify(pm.cookies.toObject()))" });
        Assert.Contains("\"cookie\":null", agents.Exchange.RawBody);
        Assert.Equal(["{}"], agents.Console);

        var uri = new Uri("https://api.shop.example/v1/orders");
        Assert.Equal(("shop.example", false, "/v1"), (PostmanCookies.Parse(uri, "a=1; Domain=.shop.example; Secure")!.Domain, PostmanCookies.Parse(uri, "a=1; Domain=.shop.example")!.HostOnly, PostmanCookies.Parse(uri, "a=1")!.Path));
        Assert.Null(PostmanCookies.Parse(uri, "a=1; Domain=example"));
        Assert.Null(PostmanCookies.Parse(uri, "a=1; Domain=other.example"));
        Assert.False(PostmanCookies.Parse(uri, "a=1; Path=/v1; Secure")!.Matches(new Uri("http://api.shop.example/v1")));
        Assert.False(PostmanCookies.Parse(uri, "a=1; Path=/v1")!.Matches(new Uri("https://api.shop.example/v10")));
        Assert.True(PostmanCookies.Parse(uri, "a=1; Expires=Wed, 21-Oct-2015 07:28:00 GMT")!.IsExpired);
    }

    // ---- OAuth 2.0 --------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Access_tokens_are_asked_for_kept_and_refreshed()
    {
        var challenge = "";
        var count = 0;
        using var server = new PostmanServer(seen =>
        {
            if (seen.Path.StartsWith("/api", StringComparison.Ordinal))
                return new PostmanServer.Answer(JsonSerializer.Serialize(new { auth = seen.Headers.GetValueOrDefault("Authorization"), seen.Path }));
            var form = seen.Body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            object answer = form["grant_type"] switch
            {
                "client_credentials" when seen.Headers.GetValueOrDefault("Authorization") == "Basic " + Convert.ToBase64String("app:s3cret"u8.ToArray()) => new { access_token = $"client-token-{++count}", expires_in = 3600 },
                "password" when form["username"] == "ada" && form["password"] == "pw" && form["client_id"] == "app" => new { access_token = "password-token", expires_in = "3600", refresh_token = "refresh-1" },
                "authorization_code" when form["code"] == "c0de" && Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_') == challenge =>
                    new { access_token = "code-token", expires_in = 1, refresh_token = "refresh-2" },
                "refresh_token" when form["refresh_token"] == "refresh-2" => new { access_token = "fresh-token", expires_in = 3600 },
                _ => new { error = "invalid_grant", error_description = "no such thing" },
            };
            var json = JsonSerializer.Serialize(answer);
            return new PostmanServer.Answer(json, json.Contains("error", StringComparison.Ordinal) ? 400 : 200);
        });
        var store = Sealed(out var vault);
        // The "browser": signs in, and goes where the sign-in says.
        Uri? opened = null;
        var answers = "code=c0de";
        void Browse(Uri uri)
        {
            opened = uri;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            challenge = query["code_challenge"] ?? "";
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                if (answers.StartsWith('#'))
                {
                    // What is after # is the page's, which comes again with it as a query.
                    Assert.Contains("location.hash", await http.GetStringAsync(query["redirect_uri"]));
                    await http.GetStringAsync($"{query["redirect_uri"]}?{answers[1..]}&state={query["state"]}");
                }
                else
                    await http.GetStringAsync($"{query["redirect_uri"]}?{answers}{(answers.Contains("state=", StringComparison.Ordinal) ? "" : "&state=" + query["state"])}");
            });
        }
        var pasted = "";
        var oauth = new PostmanOAuth(store, Browse, (_, label) => Task.FromResult<string?>(pasted));
        var item = Item(new PostmanRequest { Url = server.Url + "/api" }, out var collection);
        PostmanAuth Auth(string grant, params (string Key, string Value)[] more) => new(PostmanAuth.OAuth2, new Dictionary<string, string>
        {
            ["grant_type"] = grant, ["accessTokenUrl"] = server.Url + "/token", ["authUrl"] = server.Url + "/authorize?tenant=t", ["clientId"] = "app", ["scope"] = "read",
        }.Concat(more.Select(m => KeyValuePair.Create(m.Key, m.Value))).ToDictionary());
        Task<PostmanResult> Send(PostmanAuth auth) => new PostmanSender(null, null) { OAuth = oauth }.SendAsync(item, new PostmanRequest { Url = server.Url + "/api", Auth = auth });
        static string Said(PostmanResult result) => JsonNode.Parse(result.Exchange.RawBody)?["auth"]?.GetValue<string>() ?? result.Exchange.Error ?? "";

        // The client's own: asked for as the request is sent, then kept.
        var client = Auth(PostmanOAuth.Client, ("clientSecret", "s3cret"));
        Assert.Equal("Bearer client-token-1", Said(await Send(client)));
        Assert.Equal("Bearer client-token-1", Said(await Send(client)));
        await store.Written;
        Assert.Contains("client-token-1", vault.GetSealed(Assert.Single(Names(vault))));
        oauth.Forget(client, t => t);
        Assert.Equal("Bearer client-token-2", Said(await Send(client)));
        Assert.Contains("no such thing", (await Send(Auth(PostmanOAuth.Client, ("clientSecret", "wrong"), ("audience", "z")))).Exchange.Error);
        Assert.Equal("Bearer password-token", Said(await Send(Auth(PostmanOAuth.PasswordGrant, ("username", "ada"), ("password", "pw"), ("client_authentication", "body")))));

        // In the browser, with PKCE; ended, it is refreshed.
        var code = Auth(PostmanOAuth.Pkce);
        Assert.Contains("Get New Access Token", (await Send(code)).Exchange.Error);
        var sender = new PostmanSender(null, null) { OAuth = oauth };
        Assert.Equal("code-token", (await sender.GetTokenAsync(collection, code, default)).AccessToken);
        Assert.Contains("tenant=t&response_type=code&client_id=app&redirect_uri=http%3A%2F%2F127.0.0.1%3A", opened!.Query);
        Assert.Contains("code_challenge_method=S256", opened.Query);
        Assert.Equal("Bearer fresh-token", Said(await Send(code)));
        Assert.Equal("refresh-2", oauth.Token(code, t => t)!.RefreshToken);
        // The answer to another sign-in is not this one's; a refusal is said.
        answers = "code=c0de&state=other";
        Assert.Contains("not the answer", (await Assert.ThrowsAsync<PostmanException>(() => sender.GetTokenAsync(collection, code, default))).Message);
        answers = "error=access_denied&error_description=The+user+said+no";
        Assert.Contains("The user said no", (await Assert.ThrowsAsync<PostmanException>(() => sender.GetTokenAsync(collection, code, default))).Message);
        // The implicit grant's, after a #; one that goes back elsewhere is pasted.
        answers = "#access_token=implicit-token&expires_in=60";
        Assert.Equal("implicit-token", (await sender.GetTokenAsync(collection, Auth(PostmanOAuth.Implicit, ("addTokenTo", "queryParams")), default)).AccessToken);
        Assert.Equal("/api?access_token=implicit-token", JsonNode.Parse((await Send(Auth(PostmanOAuth.Implicit, ("addTokenTo", "queryParams")))).Exchange.RawBody)!["Path"]!.GetValue<string>());
        answers = "ignored=1";
        pasted = "https://oauth.pstmn.io/v1/callback?error=denied";
        Assert.Contains("denied", (await Assert.ThrowsAsync<PostmanException>(() => sender.GetTokenAsync(collection, Auth(PostmanOAuth.Code, ("redirect_uri", "https://oauth.pstmn.io/v1/callback")), default))).Message);

        // The user's values go to a server's token address only where they may; an agent asks for none, and uses the user's only with the environment's say.
        var environment = PostmanEnvironment.Load(Write(Folder(), "e.postman_environment.json", """{"id":"oauth","name":"E","values":[]}"""), store);
        environment.Set("secret", "s3cret");
        environment.Set("api", "http://localhost:9");
        var theirs = new PostmanSender(environment, null) { OAuth = oauth, AskHost = (_, _) => Task.FromResult(false) };
        Assert.Contains("not one of E's hosts", (await Assert.ThrowsAsync<PostmanException>(() => theirs.GetTokenAsync(collection, Auth(PostmanOAuth.Client, ("clientSecret", "{{secret}}"), ("scope2", "x")), default))).Message);
        Assert.Contains("keeps from agents", (await new PostmanSender(environment, null) { OAuth = oauth, Agent = new PostmanAgentLayer() }.SendAsync(item, new PostmanRequest { Url = server.Url + "/api", Auth = client })).Shown.Error);
        environment.AddHost(server.Url[7..]);
        environment.AgentsMaySend = true;
        var agents = await new PostmanSender(environment, null) { OAuth = oauth, Agent = new PostmanAgentLayer() }.SendAsync(item, new PostmanRequest { Url = server.Url + "/api", Auth = client, Tests = "console.log(pm.response.text())" });
        Assert.Equal("Bearer client-token-2", Said(agents));
        Assert.DoesNotContain("client-token-2", agents.Shown.RawBody + agents.Console.Single());
        Assert.Contains("the user gets one", (await new PostmanSender(environment, null) { OAuth = oauth, Agent = new PostmanAgentLayer() }.SendAsync(item, new PostmanRequest { Url = server.Url + "/api", Auth = Auth(PostmanOAuth.Client, ("clientSecret", "s3cret"), ("audience", "other")) })).Shown.Error);
    }

    /// <summary>The names of what a vault keeps sealed.</summary>
    static List<string> Names(SecretVault vault)
    {
        var path = (string)typeof(SecretVault).GetField("_path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vault)!;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sealed";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public void An_auths_lists_stay_as_a_collection_has_them()
    {
        var node = JsonNode.Parse("""{"type":"oauth2","oauth2":[{"key":"clientId","value":"a","type":"string"},{"key":"tokenRequestParams","value":[{"key":"resource","value":"r","enabled":true,"send_as":"request_body"}],"type":"any"},{"key":"useBrowser","value":false,"type":"boolean"}]}""")!.AsObject();
        var auth = PostmanAuth.Read(node)!;
        Assert.Equal(("resource", "r", "request_body"), (auth.Lists["tokenRequestParams"][0].Key, auth.Lists["tokenRequestParams"][0].Value, auth.Lists["tokenRequestParams"][0].Description));
        var changed = (auth with { Values = new Dictionary<string, string>(auth.Values) { ["clientId"] = "b" } }).ToJson(node);
        Assert.Equal("""{"type":"oauth2","oauth2":[{"key":"clientId","value":"b","type":"string"},{"key":"tokenRequestParams","value":[{"key":"resource","value":"r","enabled":true,"send_as":"request_body"}],"type":"any"},{"key":"useBrowser","value":false,"type":"boolean"}]}""", changed.ToJsonString());
        // In their rows: a grant's own, and a choice left as it is not written.
        var edits = 0;
        var fields = new PostmanAuthFields(() => edits++);
        fields.Load(PostmanAuth.Read(node));
        fields.Show(PostmanAuth.OAuth2);
        Assert.DoesNotContain(fields.Shown, f => f.IsShown && f.Key is "username" or "challengeAlgorithm" && f.IsShown);
        fields.Shown.Single(f => f.Key == "grant_type").Index = 3;
        Assert.Equal((true, false), (fields.Shown.Single(f => f.Key == "username").IsShown, fields.Shown.Single(f => f.Key == "authUrl").IsShown));
        fields.Shown.Single(f => f.Key == "username").Text = "ada";
        Assert.Equal(2, edits);
        var values = new Dictionary<string, string>(auth.Values);
        fields.Put(PostmanAuth.OAuth2, values);
        Assert.Equal(("password_credentials", "ada", "a", false), (values["grant_type"], values["username"], values["clientId"], values.ContainsKey("client_authentication")));
    }

    // ---- Data -------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_run_is_sent_with_its_data_files_rows()
    {
        Assert.Equal([("1", "x, \"y\""), ("007", "line\nbreak")], PostmanData.Csv("﻿id,note\r\n1,\"x, \"\"y\"\"\"\r\n007,\"line\nbreak\"\r\n").Select(r => (PostmanScope.Text(r["id"]), (string)r["note"]!)));
        Assert.Equal([1.5, "b"], PostmanData.Csv("a;b\n1.5;b").Single().Values);
        Assert.Empty(PostmanData.Csv("a,b\n"));
        using var server = new PostmanServer(seen => new PostmanServer.Answer("{}"));
        var folder = Folder();
        var item = Item(new PostmanRequest { Url = server.Url + "/{{id}}?n={{name}}", Tests = "pm.test('row ' + pm.iterationData.get('id'), () => { pm.expect(pm.iterationData.get('tags')).to.eql(['a']); pm.expect(pm.iterationData.toObject()).to.have.property('name'); });" }, out var collection);
        var file = Write(folder, "data.json", """[{"id":1,"name":"ada","tags":["a"]},{"id":2,"name":"bob","tags":["a"]}]""");
        var rows = PostmanData.Read(file);
        var results = await new PostmanSender(null, null).RunAsync([item], new PostmanRunOptions { Iterations = 3, Data = rows });
        Assert.All(results, r => Assert.True(r.Succeeded, r.Tests.FirstOrDefault()?.Error));
        Assert.Equal(["/1?n=ada", "/2?n=bob", "/2?n=bob"], server.Requests.Select(r => r.Path));
        Assert.Equal(["row 1", "row 2", "row 2"], results.Select(r => r.Tests.Single().Name));
        Assert.Equal([1, 2, 2], results.Select(r => r.Row));
        foreach (var (name, text, why) in new[] { ("a.json", "{}", "not an array"), ("b.json", "[1]", "not an array"), ("c.json", "[", "not JSON"), ("d.csv", "", "no rows") })
            Assert.Contains(why, Assert.Throws<PostmanException>(() => PostmanData.Read(Write(folder, name, text))).Message);
        Assert.Contains("There is no", Assert.Throws<PostmanException>(() => PostmanData.Read(Path.Combine(folder, "none.csv"))).Message);
    }
}
