using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EpsonPadReset;

internal interface IEpsonTransport : IDisposable
{
    string Name { get; }
    int? ReadEeprom(int addr);
    bool WriteEeprom(int addr, int value);
}

internal static class EpsonCmd
{
    public static byte[] EncodeFactory(ushort rkey, char letter, byte[] payload)
    {
        var c = (byte)letter;
        var head = new byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0, 2), rkey);
        head[2] = c;
        head[3] = (byte)(~c & 0xFF);
        head[4] = (byte)(((c >> 1) & 0x7F) | ((c << 7) & 0x80));
        var body = head.Concat(payload).ToArray();
        var cmd = new byte[2 + 2 + body.Length];
        cmd[0] = (byte)'|';
        cmd[1] = (byte)'|';
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(2, 2), (ushort)body.Length);
        Buffer.BlockCopy(body, 0, cmd, 4, body.Length);
        return cmd;
    }

    public static int? ParseEe(byte[] reply, int addr)
    {
        var text = Encoding.ASCII.GetString(reply);
        var m = Regex.Match(text, @"EE:([0-9A-Fa-f]{6})");
        if (!m.Success) return null;
        var hex = m.Groups[1].Value;
        var echoed = Convert.ToInt32(hex[..4], 16);
        var value = Convert.ToInt32(hex[4..], 16);
        return echoed == addr ? value : null;
    }

    public static int IndexOf(byte[] hay, byte[] needle)
    {
        for (var i = 0; i <= hay.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    public static byte[] Caesar(byte[] key) =>
        key.Select(b => (byte)(b == 0 ? 0 : b + 1)).ToArray();
}

/// <summary>ESC/REMOTE path from PadZero usb_direct.py (works on some models, not ET-2820).</summary>
internal sealed class EscRemoteTransport : IEpsonTransport
{
    private static readonly byte[] Init = [0x1B, (byte)'@'];
    private static readonly byte[] ExitRemote = [0x1B, 0x00, 0x00, 0x00];
    private readonly UsbPrint.Device _dev;
    private readonly ushort _rkey;
    private readonly byte[] _wkeyPlain;
    public string Name => "ESC/REMOTE";

    public EscRemoteTransport(UsbPrint.Device device, ushort readKey, string writeKeyPlain)
    {
        _dev = device; _rkey = readKey; _wkeyPlain = Encoding.ASCII.GetBytes(writeKeyPlain);
    }

    public void Dispose() { }

    private static byte[] BuildRemoteMode()
    {
        var payload = new byte[] { 0x00 }.Concat(Encoding.ASCII.GetBytes("REMOTE1")).ToArray();
        var packet = new byte[3 + 2 + payload.Length];
        packet[0] = 0x1B; packet[1] = (byte)'('; packet[2] = (byte)'R';
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(3, 2), (ushort)payload.Length);
        Buffer.BlockCopy(payload, 0, packet, 5, payload.Length);
        return packet;
    }

    private byte[] Wrap(params byte[][] cmds)
    {
        var remote = BuildRemoteMode();
        using var ms = new MemoryStream();
        ms.Write(Init); ms.Write(Init); ms.Write(remote);
        foreach (var c in cmds) ms.Write(c);
        ms.Write(ExitRemote); ms.Write(Init);
        return ms.ToArray();
    }

    private byte[] Exchange(byte[] data, string? want, int tries = 8)
    {
        for (var i = 0; i < 3; i++)
            if (_dev.Read().Length == 0) break;
        _dev.Write(data);
        using var seen = new MemoryStream();
        var wantBytes = want is null ? null : Encoding.ASCII.GetBytes(want);
        for (var i = 0; i < tries; i++)
        {
            var chunk = _dev.Read();
            if (chunk.Length == 0) continue;
            seen.Write(chunk);
            if (wantBytes is not null && EpsonCmd.IndexOf(seen.ToArray(), wantBytes) >= 0) break;
        }
        return seen.ToArray();
    }

    public int? ReadEeprom(int addr)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), (ushort)addr);
        return EpsonCmd.ParseEe(Exchange(Wrap(EpsonCmd.EncodeFactory(_rkey, 'A', payload)), "EE:"), addr);
    }

    public bool WriteEeprom(int addr, int value)
    {
        var caesar = EpsonCmd.Caesar(_wkeyPlain);
        var payload = new byte[2 + 1 + caesar.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), (ushort)addr);
        payload[2] = (byte)value;
        Buffer.BlockCopy(caesar, 0, payload, 3, caesar.Length);
        var reply = Exchange(Wrap(EpsonCmd.EncodeFactory(_rkey, 'B', payload)), ":OK;");
        return Encoding.ASCII.GetString(reply).Contains(":OK;") && ReadEeprom(addr) == value;
    }
}

/// <summary>
/// IEEE 1284.4 for Epson ET-2820 family. Negotiates revision 0x10 when required
/// (OpenChannel/CreditRequest use the longer 0x10 layouts from reinkpy).
/// </summary>
internal sealed class D4Transport : IEpsonTransport
{
    private static readonly byte[] EnterD4 =
    [
        0x00, 0x00, 0x00, 0x1B, 0x01,
        (byte)'@', (byte)'E', (byte)'J', (byte)'L', (byte)' ',
        (byte)'1', (byte)'2', (byte)'8', (byte)'4', (byte)'.', (byte)'4', (byte)'\n',
        (byte)'@', (byte)'E', (byte)'J', (byte)'L', (byte)'\n',
        (byte)'@', (byte)'E', (byte)'J', (byte)'L', (byte)'\n'
    ];
    private static readonly byte[] EnterD4Reply = [0x00, 0x00, 0x00, 0x08, 0x01, 0x00, 0xC5, 0x00];

    private readonly UsbPrint.Device _dev;
    private readonly ushort _rkey;
    private readonly byte[] _wkeyShifted;
    private readonly bool _verbose;
    private readonly Dictionary<(byte, byte), int> _credit = new();
    private byte[] _rxBuf = Array.Empty<byte>();
    private bool _open;
    private byte _revision = 0x20; // may drop to 0x10 after InitReply

    public string Name => $"IEEE1284.4/D4(rev=0x{_revision:X2})";

    public D4Transport(UsbPrint.Device device, ushort readKey, string writeKeyPlain, bool verbose = false)
    {
        _dev = device;
        _rkey = readKey;
        _wkeyShifted = EpsonCmd.Caesar(Encoding.ASCII.GetBytes(writeKeyPlain));
        _verbose = verbose;
    }

    public void Open()
    {
        if (_open) return;

        while (_dev.Read().Length > 0) { /* drain */ }

        _dev.Write(EnterD4);
        var seen = ReadRawUntil(EnterD4Reply, tries: 10);
        if (_verbose) Console.WriteLine($"  [D4] enter: {Convert.ToHexString(seen)}");
        if (EpsonCmd.IndexOf(seen, EnterD4Reply) < 0 &&
            !(seen.Length >= 4 && seen[0] == 0 && seen[1] == 0 && seen[2] == 0))
            throw new InvalidOperationException(
                "D4-Einstieg fehlgeschlagen." +
                (seen.Length == 0 ? " Keine Daten." : $" Empfangen: {Convert.ToHexString(seen)}"));

        _credit[(0, 0)] = 0;
        _rxBuf = Array.Empty<byte>(); // discard non-packet enter framing leftovers

        // Init: try 0x20, fall back to peer revision (ET-2820 wants 0x10)
        var init = TxnInit(0x20);
        if (init is null) throw new InvalidOperationException("D4 Init fehlgeschlagen.");
        if (init[0] == 0x02 && init.Length >= 2)
        {
            _revision = init[1];
            if (_verbose) Console.WriteLine($"  [D4] peer verlangt Revision 0x{_revision:X2}");
            init = TxnInit(_revision);
        }
        else if (init[0] == 0x00 && init.Length >= 2)
            _revision = init[1];

        if (init is null || init[0] != 0x00)
            throw new InvalidOperationException($"D4 Init result=0x{init?[0]:X2}");

        // OpenChannel EPSON-CTRL (0x02,0x02)
        var ocArgs = _revision == 0x10
            ? PackOpenChannel10(0x02, 0x02)
            : PackOpenChannel20(0x02, 0x02);
        var oc = Txn(0x01, ocArgs);
        if (_verbose) Console.WriteLine($"  [D4] open: {(oc is null ? "null" : Convert.ToHexString(oc))}");
        if (oc is null || oc[0] != 0x00)
            throw new InvalidOperationException($"OpenChannel EPSON-CTRL fehlgeschlagen (result=0x{oc?[0]:X2}).");

        // OpenChannelReply: result sidP sidS maxPTS maxSTP maxCredit [grantedCredit]
        // ET-2820 oft ohne/granted=0 → CreditRequest ist Pflicht (kein Fake-Credit!)
        _credit[(0x02, 0x02)] = 0;
        if (oc.Length >= 11)
            _credit[(0x02, 0x02)] = BinaryPrimitives.ReadUInt16BigEndian(oc.AsSpan(9, 2));
        if (_verbose) Console.WriteLine($"  [D4] CTRL credits after open: {Credits((0x02, 0x02))}");

        EnsureChannelCredits((0x02, 0x02));
        if (Credits((0x02, 0x02)) < 1)
            throw new InvalidOperationException("CTRL-Kanal hat nach CreditRequest keine Credits.");
        _open = true;
    }

    public void Dispose()
    {
        if (!_open) return;
        try { Txn(0x02, [0x02, 0x02]); Txn(0x08, Array.Empty<byte>()); } catch { }
        _open = false;
    }

    public int? ReadEeprom(int addr)
    {
        Open();
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), (ushort)addr);
        var reply = Ctrl(EpsonCmd.EncodeFactory(_rkey, 'A', payload));
        return EpsonCmd.ParseEe(reply, addr);
    }

    public bool WriteEeprom(int addr, int value)
    {
        Open();
        var payload = new byte[2 + 1 + _wkeyShifted.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), (ushort)addr);
        payload[2] = (byte)value;
        Buffer.BlockCopy(_wkeyShifted, 0, payload, 3, _wkeyShifted.Length);
        var reply = Ctrl(EpsonCmd.EncodeFactory(_rkey, 'B', payload));
        return Encoding.ASCII.GetString(reply).Contains(":OK;") && ReadEeprom(addr) == value;
    }

    private byte[] Ctrl(byte[] payload)
    {
        EnsureChannelCredits((0x02, 0x02));
        if (Credits((0x02, 0x02)) < 1)
            throw new InvalidOperationException("Kein CTRL-Credit vor EEPROM-Befehl.");

        SendPacket((0x02, 0x02), payload, checkCredit: true);
        using var ms = new MemoryStream();
        for (var i = 0; i < 14; i++)
        {
            var pkt = ReceivePacket();
            if (pkt is null)
            {
                if (ms.Length > 0) break;
                continue;
            }
            var (psid, ssid, credit, data) = pkt.Value;
            AddCredits((psid, ssid), credit);
            if (psid == 0 && ssid == 0) { HandleTx(data); continue; }
            if (psid == 0x02 && ssid == 0x02 && data.Length > 0)
            {
                if (_verbose) Console.WriteLine($"  [D4] CTRL data ({data.Length} B): {Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 64)))}");
                ms.Write(data);
                if (Array.IndexOf(data, (byte)';') >= 0) break;
            }
        }
        return ms.ToArray();
    }

    private void EnsureChannelCredits((byte, byte) cid)
    {
        for (var i = 0; i < 6 && Credits(cid) < 1; i++)
        {
            // Make sure TX channel can send CreditRequest (needs a piggybacked TX credit)
            if (Credits((0, 0)) < 1)
            {
                // CreditRequest for TX itself (0,0) — still often answered via piggyback on later packets
                var txReq = _revision == 0x10
                    ? new byte[] { 0x04, 0x00, 0x00, 0x00, 0x80, 0xFF, 0xFF }
                    : new byte[] { 0x04, 0x00, 0x00, 0x00, 0x00 };
                SendPacket((0, 0), txReq, checkCredit: false);
                Pump(0x84, 8);
            }

            // CreditRequest for target channel — reinkpy protocol_0x10: BBHH (x1=0x0080, x2=0xffff)
            byte[] payload = _revision == 0x10
                ? [0x04, cid.Item1, cid.Item2, 0x00, 0x80, 0xFF, 0xFF]
                : [0x04, cid.Item1, cid.Item2, 0x00, 0x00];

            var useTxCredit = Credits((0, 0)) >= 1;
            SendPacket((0, 0), payload, checkCredit: useTxCredit);
            var reply = Pump(0x84, 10);
            if (_verbose)
                Console.WriteLine($"  [D4] CreditRequest {cid} -> {(reply is null ? "null" : Convert.ToHexString(reply))} credits={Credits(cid)}");
        }
    }

    private byte[]? TxnInit(byte revision)
    {
        var payload = new byte[] { 0x00, revision }; // Init + rev
        SendPacket((0, 0), payload, checkCredit: false);
        return Pump(0x80, 12);
    }

    private byte[]? Txn(byte code, byte[] args)
    {
        var replyCode = (byte)(code | 0x80);
        var payload = new byte[1 + args.Length];
        payload[0] = code;
        Buffer.BlockCopy(args, 0, payload, 1, args.Length);

        if (code is not (0x00 or 0x04) && Credits((0, 0)) < 1)
        {
            // request TX credit
            SendPacket((0, 0), [0x04, 0x00, 0x00, 0x00, 0x00], checkCredit: false);
            Pump(0x84, 6);
        }

        var needCredit = code is not (0x00 or 0x04);
        SendPacket((0, 0), payload, checkCredit: needCredit);
        return Pump(replyCode, 12);
    }

    private byte[]? Pump(byte replyWant, int tries)
    {
        for (var i = 0; i < tries; i++)
        {
            var pkt = ReceivePacket();
            if (pkt is null) continue;
            var (psid, ssid, credit, payload) = pkt.Value;
            AddCredits((psid, ssid), credit);
            if (psid == 0 && ssid == 0 && payload.Length > 0)
            {
                HandleTx(payload);
                if (payload[0] == replyWant) return payload[1..];
            }
        }
        return null;
    }

    private void HandleTx(byte[] payload)
    {
        // CreditRequestReply 0x84: result, sidP, sidS, addCredit(u16 BE)
        if (payload.Length >= 6 && payload[0] == 0x84)
        {
            if (payload[1] != 0x00)
            {
                if (_verbose) Console.WriteLine($"  [D4] CreditRequest result=0x{payload[1]:X2}");
                return;
            }
            var add = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2));
            AddCredits((payload[2], payload[3]), add);
            if (_verbose) Console.WriteLine($"  [D4] +{add} credits -> ({payload[2]},{payload[3]}) now {Credits((payload[2], payload[3]))}");
        }
        else if (payload.Length > 0 && payload[0] == 0x7F && _verbose)
        {
            Console.WriteLine($"  [D4] Error packet: {Convert.ToHexString(payload)}");
        }
    }

    private int Credits((byte, byte) cid) => _credit.TryGetValue(cid, out var c) ? c : 0;
    private void AddCredits((byte, byte) cid, int amount) => _credit[cid] = Credits(cid) + amount;

    private void SendPacket((byte psid, byte ssid) cid, byte[] payload, bool checkCredit)
    {
        if (checkCredit)
        {
            var c = Credits(cid);
            if (c < 1) throw new InvalidOperationException($"Keine D4-Credits für {cid}");
            _credit[cid] = c - 1;
        }
        var length = (ushort)(6 + payload.Length);
        var pkt = new byte[6 + payload.Length];
        pkt[0] = cid.psid; pkt[1] = cid.ssid;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2, 2), length);
        pkt[4] = 1; pkt[5] = 0;
        Buffer.BlockCopy(payload, 0, pkt, 6, payload.Length);
        _dev.Write(pkt);
    }

    private (byte psid, byte ssid, int credit, byte[] payload)? ReceivePacket()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (_rxBuf.Length < 6)
            {
                var chunk = _dev.Read();
                if (chunk.Length == 0) continue;
                _rxBuf = _rxBuf.Concat(chunk).ToArray();
            }
            if (_rxBuf.Length < 6) continue;

            // Skip non-packet framing left from enter (00 00 00 08 01 00 c5 00)
            // Real D4 packets have length field (bytes 2-3 BE) >= 6 and usually < 0x400
            var length = BinaryPrimitives.ReadUInt16BigEndian(_rxBuf.AsSpan(2, 2));
            if (length < 6 || length > 0x800)
            {
                _rxBuf = _rxBuf[1..];
                continue;
            }
            while (_rxBuf.Length < length)
            {
                var chunk = _dev.Read();
                if (chunk.Length == 0) break;
                _rxBuf = _rxBuf.Concat(chunk).ToArray();
            }
            if (_rxBuf.Length < length) continue;

            var psid = _rxBuf[0];
            var ssid = _rxBuf[1];
            var credit = _rxBuf[4];
            var payload = _rxBuf[6..length];
            _rxBuf = _rxBuf[length..];
            return (psid, ssid, credit, payload);
        }
        return null;
    }

    private byte[] ReadRawUntil(byte[] marker, int tries)
    {
        using var ms = new MemoryStream();
        for (var i = 0; i < tries; i++)
        {
            var chunk = _dev.Read();
            if (chunk.Length == 0) continue;
            ms.Write(chunk);
            if (EpsonCmd.IndexOf(ms.ToArray(), marker) >= 0) break;
        }
        return ms.ToArray();
    }

    private static byte[] PackOpenChannel20(byte sidP, byte sidS)
    {
        // BBHHH: sidP sidS maxPTS maxSTP maxCredit
        var b = new byte[8];
        b[0] = sidP; b[1] = sidS;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6, 2), 0x0000);
        return b;
    }

    private static byte[] PackOpenChannel10(byte sidP, byte sidS)
    {
        // BBHHHH: sidP sidS maxPTS maxSTP maxCredit initCredit
        var b = new byte[10];
        b[0] = sidP; b[1] = sidS;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6, 2), 0x0000);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8, 2), 0x0000);
        return b;
    }
}

internal sealed class EpsonProtocol : IDisposable
{
    private readonly UsbPrint.Device _dev;
    private readonly ushort _rkey;
    private readonly string _wkey;
    private readonly bool _verbose;
    private IEpsonTransport? _transport;

    public string TransportName => _transport?.Name ?? "(nicht verbunden)";

    public EpsonProtocol(UsbPrint.Device device, ushort readKey = 0x364A, string writeKey = "Maribaya", bool verbose = false)
    {
        _dev = device;
        _rkey = readKey;
        _wkey = writeKey;
        _verbose = verbose;
        _dev.ReadTimeoutMs = 1500;
    }

    public void Connect()
    {
        if (_transport is not null) return;

        if (_verbose)
        {
            var id = _dev.TryGet1284Id();
            if (id is not null) Console.WriteLine($"  1284-ID: {id}");
        }

        Exception? d4Err = null, escErr = null;

        // ET-2820: D4 first (ESC/REMOTE returns empty on this model)
        try
        {
            if (_verbose) Console.WriteLine("  Versuche IEEE 1284.4 / D4...");
            var d4 = new D4Transport(_dev, _rkey, _wkey, _verbose);
            d4.Open();
            var v = d4.ReadEeprom(47);
            if (v is not null)
            {
                _transport = d4;
                if (_verbose) Console.WriteLine($"  D4 OK (EEPROM[47]={v})");
                return;
            }
            d4.Dispose();
            d4Err = new InvalidOperationException("D4 offen, aber keine EE:-Antwort");
        }
        catch (Exception ex) { d4Err = ex; }

        try
        {
            if (_verbose) Console.WriteLine("  Versuche ESC/REMOTE...");
            var esc = new EscRemoteTransport(_dev, _rkey, _wkey);
            var v = esc.ReadEeprom(47);
            if (v is not null)
            {
                _transport = esc;
                if (_verbose) Console.WriteLine($"  ESC/REMOTE OK (EEPROM[47]={v})");
                return;
            }
            escErr = new InvalidOperationException("ESC/REMOTE: keine EE:-Antwort");
        }
        catch (Exception ex) { escErr = ex; }

        throw new InvalidOperationException(
            "Weder D4 noch ESC/REMOTE konnten EEPROM lesen.\n" +
            $"  D4: {d4Err?.Message}\n" +
            $"  ESC/REMOTE: {escErr?.Message}");
    }

    public int? ReadEeprom(int addr) { Connect(); return _transport!.ReadEeprom(addr); }
    public bool WriteEeprom(int addr, int value) { Connect(); return _transport!.WriteEeprom(addr, value); }

    public Dictionary<int, int?> DumpEeprom(int first = 0, int last = 0xFF)
    {
        Connect();
        var map = new Dictionary<int, int?>();
        for (var a = first; a <= last; a++) map[a] = _transport!.ReadEeprom(a);
        return map;
    }

    public void Dispose() => _transport?.Dispose();
}

internal sealed class ModelDb
{
    public Dictionary<string, ModelEntry> Models { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static ModelDb Load(string path)
    {
        var json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize<Dictionary<string, ModelEntry>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        return new ModelDb { Models = new(raw, StringComparer.OrdinalIgnoreCase) };
    }

    public ModelEntry Resolve(string model)
    {
        if (!Models.TryGetValue(model, out var e))
            throw new InvalidOperationException($"Modell '{model}' nicht in models.json.");
        return string.IsNullOrEmpty(e.AliasOf) ? e : Resolve(e.AliasOf);
    }
}

internal sealed class ModelEntry
{
    [JsonPropertyName("_alias_of")] public string? AliasOf { get; set; }
    [JsonPropertyName("raw_waste_reset")] public Dictionary<string, int>? RawWasteReset { get; set; }
    [JsonPropertyName("read_key")] public int[]? ReadKey { get; set; }
    [JsonPropertyName("write_key")] public string? WriteKey { get; set; }
    [JsonPropertyName("waste")] public Dictionary<string, WasteCounter>? Waste { get; set; }

    public ushort GetReadKey() =>
        ReadKey is { Length: >= 2 } ? (ushort)(ReadKey[0] | (ReadKey[1] << 8)) : (ushort)0x364A;
    public string GetWriteKey() => WriteKey ?? "Maribaya";

    public IReadOnlyList<(int Addr, int Value)> GetResetPlan()
    {
        if (RawWasteReset is null || RawWasteReset.Count == 0)
            throw new InvalidOperationException("Kein raw_waste_reset für dieses Modell.");
        return RawWasteReset.Select(kv => (int.Parse(kv.Key), kv.Value)).OrderBy(x => x.Item1).ToList();
    }
}

internal sealed class WasteCounter
{
    [JsonPropertyName("divider")] public double? Divider { get; set; }
    [JsonPropertyName("oids")] public int[]? Oids { get; set; }
}
