using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LiteTunnel.Core;

/// <summary>
/// Is a QUIC server there: sends one QUIC v1 Initial with a TLS 1.3
/// ClientHello (RFC 9000, RFC 9001) and times the first datagram back. The
/// answer is not read: a ServerHello, a Retry or a close all mean the server
/// is up. One packet out, no handshake, no certificate check.
/// </summary>
public static class QuicProbe
{
    private static readonly byte[] InitialSalt = Hex("38762cf7f55934b34d179ae6a4c80cadccbb7f0a");

    public static async Task<PingResult> ProbeAsync(
        IPEndPoint address, string sni, IReadOnlyList<string> alpn, int timeoutMs,
        Action<Socket>? bind = null, CancellationToken cancellation = default)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            bind?.Invoke(socket);
            await socket.ConnectAsync(address, cancellation).ConfigureAwait(false);
            var packet = InitialPacket(RandomNumberGenerator.GetBytes(8), RandomNumberGenerator.GetBytes(8), sni, alpn);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(timeoutMs);
            var clock = Stopwatch.StartNew();
            await socket.SendAsync(packet, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            var buffer = new byte[1500];
            await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            return PingResult.Ms(Math.Max(1, (int)clock.ElapsedMilliseconds));
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
            return PingResult.Failed;
        }
    }

    public sealed record Keys(byte[] Key, byte[] Iv, byte[] Hp);

    /// <summary>Client Initial keys for a destination connection ID (RFC 9001 §5.2).</summary>
    public static Keys ClientKeys(byte[] dcid)
    {
        var initial = HMACSHA256.HashData(InitialSalt, dcid);
        var client = ExpandLabel(initial, "client in", 32);
        return new Keys(ExpandLabel(client, "quic key", 16), ExpandLabel(client, "quic iv", 12), ExpandLabel(client, "quic hp", 16));
    }

    /// <summary>The header protection mask for a 16-byte sample (RFC 9001 §5.4.3).</summary>
    public static byte[] HeaderMask(byte[] hp, ReadOnlySpan<byte> sample)
    {
        using var aes = Aes.Create();
        aes.Key = hp;
        return aes.EncryptEcb(sample[..16], PaddingMode.None);
    }

    /// <summary>A protected Initial packet, padded to the 1200 bytes servers require.</summary>
    public static byte[] InitialPacket(byte[] dcid, byte[] scid, string sni, IReadOnlyList<string> alpn)
    {
        var keys = ClientKeys(dcid);
        const int pnLength = 4, tagSize = 16;
        var header = new Bytes();
        header.U8(0xC0 | (pnLength - 1)); // long header, Initial
        header.U32(0x00000001);
        header.U8(dcid.Length); header.Raw(dcid);
        header.U8(scid.Length); header.Raw(scid);
        header.U8(0); // token length
        int headerSize = header.Length + 2 + pnLength; // + 2-byte length + packet number

        var hello = ClientHello(sni, alpn, scid);
        var crypto = new Bytes();
        crypto.U8(0x06); // CRYPTO frame
        crypto.Varint(0);
        crypto.Varint(hello.Length);
        crypto.Raw(hello);
        var payload = crypto.ToArray();
        Array.Resize(ref payload, Math.Max(payload.Length, 1200 - headerSize - tagSize)); // the rest is PADDING (0x00)

        header.U16(0x4000 | (pnLength + payload.Length + tagSize));
        header.U32(0); // packet number 0
        var aad = header.ToArray();

        var sealedPayload = new byte[payload.Length];
        var tag = new byte[tagSize];
        using (var gcm = new AesGcm(keys.Key, tagSize))
            gcm.Encrypt(keys.Iv, payload, sealedPayload, tag, aad); // nonce = iv xor pn 0

        var packet = new byte[aad.Length + sealedPayload.Length + tagSize];
        aad.CopyTo(packet, 0);
        sealedPayload.CopyTo(packet, aad.Length);
        tag.CopyTo(packet, aad.Length + sealedPayload.Length);

        int pnOffset = aad.Length - pnLength;
        var mask = HeaderMask(keys.Hp, packet.AsSpan(pnOffset + 4, 16));
        packet[0] ^= (byte)(mask[0] & 0x0f);
        for (int i = 0; i < pnLength; i++) packet[pnOffset + i] ^= mask[1 + i];
        return packet;
    }

    private static byte[] ClientHello(string sni, IReadOnlyList<string> alpn, byte[] scid)
    {
        var extensions = new Bytes();
        void Ext(int type, Bytes body)
        {
            extensions.U16(type); extensions.U16(body.Length); extensions.Raw(body.ToArray());
        }

        if (sni.Length > 0 && !ShareLink.IsIPAddress(sni))
        {
            var name = Encoding.UTF8.GetBytes(sni);
            var b = new Bytes();
            b.U16(name.Length + 3); b.U8(0); b.U16(name.Length); b.Raw(name);
            Ext(0x0000, b);
        }
        {
            var b = new Bytes();
            b.U16(4); b.U16(0x001d); b.U16(0x0017); // x25519, secp256r1
            Ext(0x000a, b);
        }
        {
            int[] signatures = [0x0403, 0x0804, 0x0401, 0x0503, 0x0805, 0x0501, 0x0806, 0x0601, 0x0807];
            var b = new Bytes();
            b.U16(signatures.Length * 2);
            foreach (var s in signatures) b.U16(s);
            Ext(0x000d, b);
        }
        {
            var protocols = new Bytes();
            foreach (var p in alpn)
            {
                var bytes = Encoding.UTF8.GetBytes(p);
                protocols.U8(bytes.Length); protocols.Raw(bytes);
            }
            var b = new Bytes();
            b.U16(protocols.Length); b.Raw(protocols.ToArray());
            Ext(0x0010, b);
        }
        {
            var b = new Bytes();
            b.U8(2); b.U16(0x0304); // TLS 1.3 only
            Ext(0x002b, b);
        }
        {
            // Any 32 bytes are a valid X25519 public key; the answer is never read.
            var b = new Bytes();
            b.U16(36); b.U16(0x001d); b.U16(32); b.Raw(RandomNumberGenerator.GetBytes(32));
            Ext(0x0033, b);
        }
        {
            var b = new Bytes();
            b.Parameter(0x01, 30_000); // max_idle_timeout
            b.Parameter(0x04, 1 << 20); // initial_max_data
            b.Parameter(0x05, 1 << 18);
            b.Parameter(0x06, 1 << 18);
            b.Parameter(0x07, 1 << 18);
            b.Parameter(0x08, 100); // initial_max_streams_bidi
            b.Parameter(0x09, 100);
            b.Varint(0x0f); b.Varint(scid.Length); b.Raw(scid); // initial_source_connection_id
            Ext(0x0039, b);
        }

        var body = new Bytes();
        body.U16(0x0303);
        body.Raw(RandomNumberGenerator.GetBytes(32));
        body.U8(0); // legacy_session_id: empty in QUIC
        body.U16(6); body.U16(0x1301); body.U16(0x1302); body.U16(0x1303);
        body.U8(1); body.U8(0); // compression: null
        body.U16(extensions.Length); body.Raw(extensions.ToArray());

        var hello = new Bytes();
        hello.U8(0x01);
        hello.U8(body.Length >>> 16); hello.U16(body.Length & 0xffff);
        hello.Raw(body.ToArray());
        return hello.ToArray();
    }

    /// <summary>HKDF-Expand-Label with an empty context (RFC 8446 §7.1); one block is enough here.</summary>
    private static byte[] ExpandLabel(byte[] secret, string label, int length)
    {
        var full = Encoding.ASCII.GetBytes($"tls13 {label}");
        var info = new Bytes();
        info.U16(length); info.U8(full.Length); info.Raw(full); info.U8(0); info.U8(1);
        return HMACSHA256.HashData(secret, info.ToArray())[..length];
    }

    internal static byte[] Hex(string s) => Convert.FromHexString(s);

    private sealed class Bytes
    {
        private readonly MemoryStream stream = new();

        public int Length => (int)stream.Length;

        public byte[] ToArray() => stream.ToArray();

        public void U8(int v) => stream.WriteByte((byte)v);

        public void Raw(ReadOnlySpan<byte> bytes) => stream.Write(bytes);

        public void U16(int v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
            stream.Write(b);
        }

        public void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            stream.Write(b);
        }

        public void Varint(long v)
        {
            if (v < 0x40) U8((int)v);
            else if (v < 0x4000) U16(0x4000 | (int)v);
            else if (v < 0x40000000) U32(0x80000000u | (uint)v);
            else
            {
                U32((uint)(0xC0000000L | (v >>> 32)));
                U32((uint)v);
            }
        }

        public void Parameter(int id, long value)
        {
            var encoded = new Bytes();
            encoded.Varint(value);
            Varint(id); Varint(encoded.Length); Raw(encoded.ToArray());
        }
    }
}
