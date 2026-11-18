using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuardianX.Core.Power;

public sealed class PlugConfig
{
    public string Kind { get; set; } = "none";   // none, shelly, tapo, simulated
    public string Host { get; set; } = "";        // IP address on your Wi-Fi
    public string Username { get; set; } = "";    // Tapo account email
    public string Password { get; set; } = "";    // Tapo account password, stored with Windows DPAPI
}

/// <summary>A Wi-Fi smart plug that the laptop charger is plugged into.</summary>
public interface ISmartPlug
{
    string Name { get; }
    Task<bool?> IsOnAsync(CancellationToken ct = default);
    Task SetAsync(bool on, CancellationToken ct = default);
}

public static class SmartPlugs
{
    public static ISmartPlug? Create(PlugConfig c) => c.Kind switch
    {
        "shelly" when c.Host != "" => new ShellyPlug(c.Host),
        "tapo" when c.Host != "" => new TapoPlug(c.Host, c.Username, c.Password),
        "simulated" => SimulatedPlug.Instance,
        _ => null
    };

    internal static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(6) };
}

/// <summary>For trying the feature without hardware: remembers on/off, nothing else.</summary>
public sealed class SimulatedPlug : ISmartPlug
{
    public static readonly SimulatedPlug Instance = new();
    private bool _on = true;
    public string Name => "Simulated plug";
    public Task<bool?> IsOnAsync(CancellationToken ct = default) => Task.FromResult<bool?>(_on);
    public Task SetAsync(bool on, CancellationToken ct = default) { _on = on; return Task.CompletedTask; }
}

/// <summary>Shelly plugs, local HTTP API. Gen 1 (/relay/0) and Gen 2+ (/rpc/Switch.*) are both handled.</summary>
public sealed class ShellyPlug(string host) : ISmartPlug
{
    private int? _gen;
    public string Name => $"Shelly at {host}";

    private async Task<int> GenAsync(CancellationToken ct)
    {
        if (_gen is int g) return g;
        var info = JsonNode.Parse(await SmartPlugs.Http.GetStringAsync($"http://{host}/shelly", ct));
        _gen = info?["gen"]?.GetValue<int>() ?? 1;
        return _gen.Value;
    }

    public async Task<bool?> IsOnAsync(CancellationToken ct = default)
    {
        if (await GenAsync(ct) >= 2)
            return JsonNode.Parse(await SmartPlugs.Http.GetStringAsync($"http://{host}/rpc/Switch.GetStatus?id=0", ct))?["output"]?.GetValue<bool>();
        return JsonNode.Parse(await SmartPlugs.Http.GetStringAsync($"http://{host}/relay/0", ct))?["ison"]?.GetValue<bool>();
    }

    public async Task SetAsync(bool on, CancellationToken ct = default)
    {
        var url = await GenAsync(ct) >= 2
            ? $"http://{host}/rpc/Switch.Set?id=0&on={(on ? "true" : "false")}"
            : $"http://{host}/relay/0?turn={(on ? "on" : "off")}";
        (await SmartPlugs.Http.GetAsync(url, ct)).EnsureSuccessStatusCode();
    }
}

/// <summary>
/// TP-Link Tapo plugs (P100/P105/P110/P115) over their local "KLAP" protocol: a two-step handshake that proves
/// both sides know the account credentials, then AES-encrypted JSON requests. Nothing goes through TP-Link's cloud.
/// </summary>
public sealed class TapoPlug(string host, string username, string password) : ISmartPlug
{
    private KlapSession? _session;
    public string Name => $"Tapo at {host}";

    public async Task<bool?> IsOnAsync(CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["method"] = "get_device_info" }, ct);
        return r?["result"]?["device_on"]?.GetValue<bool>();
    }

    public async Task SetAsync(bool on, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject
        {
            ["method"] = "set_device_info",
            ["params"] = new JsonObject { ["device_on"] = on }
        }, ct);
        var code = r?["error_code"]?.GetValue<int>() ?? -1;
        if (code != 0) throw new InvalidOperationException($"Tapo refused the change (error {code}).");
    }

    private async Task<JsonNode?> RequestAsync(JsonObject payload, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            _session ??= await HandshakeAsync(ct);
            var (seq, body) = _session.Encrypt(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://{host}/app/request?seq={seq}") { Content = new ByteArrayContent(body) };
            req.Headers.Add("Cookie", _session.Cookie);
            using var res = await SmartPlugs.Http.SendAsync(req, ct);
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { _session = null; continue; }
            res.EnsureSuccessStatusCode();
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            return JsonNode.Parse(Encoding.UTF8.GetString(_session.Decrypt(seq, bytes)));
        }
        throw new InvalidOperationException("The Tapo plug rejected the session. Check the email and password.");
    }

    private async Task<KlapSession> HandshakeAsync(CancellationToken ct)
    {
        var auth = KlapSession.AuthHash(username, password);
        var local = RandomNumberGenerator.GetBytes(16);
        using var r1 = await SmartPlugs.Http.PostAsync($"http://{host}/app/handshake1", new ByteArrayContent(local), ct);
        r1.EnsureSuccessStatusCode();
        var b1 = await r1.Content.ReadAsByteArrayAsync(ct);
        if (b1.Length < 48) throw new InvalidOperationException("Unexpected reply from the Tapo plug.");
        var remote = b1[..16];
        var serverHash = b1[16..48];
        if (!serverHash.AsSpan().SequenceEqual(KlapSession.Sha256(local, remote, auth)))
            throw new InvalidOperationException("The Tapo plug didn't accept these credentials. Use the email and password of your Tapo app account.");
        var cookie = r1.Headers.TryGetValues("Set-Cookie", out var v) ? v.First().Split(';')[0] : "";
        using var req2 = new HttpRequestMessage(HttpMethod.Post, $"http://{host}/app/handshake2")
        {
            Content = new ByteArrayContent(KlapSession.Sha256(remote, local, auth))
        };
        req2.Headers.Add("Cookie", cookie);
        using var r2 = await SmartPlugs.Http.SendAsync(req2, ct);
        r2.EnsureSuccessStatusCode();
        return new KlapSession(local, remote, auth, cookie);
    }
}

/// <summary>KLAP encryption state. Kept separate from the HTTP code so it can be unit tested.</summary>
public sealed class KlapSession
{
    private readonly byte[] _key, _ivPrefix, _sig;
    private int _seq;
    public string Cookie { get; }

    public KlapSession(byte[] local, byte[] remote, byte[] auth, string cookie)
    {
        Cookie = cookie;
        _key = Sha256(Encoding.ASCII.GetBytes("lsk"), local, remote, auth)[..16];
        var iv = Sha256(Encoding.ASCII.GetBytes("iv"), local, remote, auth);
        _ivPrefix = iv[..12];
        _seq = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(iv.AsSpan(28, 4));
        _sig = Sha256(Encoding.ASCII.GetBytes("ldk"), local, remote, auth)[..28];
    }

    public static byte[] AuthHash(string user, string pass) =>
        Sha256(SHA1.HashData(Encoding.UTF8.GetBytes(user)), SHA1.HashData(Encoding.UTF8.GetBytes(pass)));

    public static byte[] Sha256(params byte[][] parts) => SHA256.HashData(parts.SelectMany(p => p).ToArray());

    private byte[] Iv(int seq)
    {
        var iv = new byte[16];
        _ivPrefix.CopyTo(iv, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(12), seq);
        return iv;
    }

    public (int Seq, byte[] Body) Encrypt(byte[] plain)
    {
        int seq = Interlocked.Increment(ref _seq);
        using var aes = Aes.Create();
        aes.Key = _key;
        var cipher = aes.EncryptCbc(plain, Iv(seq), PaddingMode.PKCS7);
        var seqBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(seqBytes, seq);
        var signature = Sha256(_sig, seqBytes, cipher);
        return (seq, signature.Concat(cipher).ToArray());
    }

    public byte[] Decrypt(int seq, byte[] body)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        return aes.DecryptCbc(body.AsSpan(32), Iv(seq), PaddingMode.PKCS7);
    }
}
