package app.litetunnel.core

import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Is a QUIC server there: sends one QUIC v1 Initial with a TLS 1.3
 * ClientHello (RFC 9000, RFC 9001) and times the first datagram back. The
 * answer is not read: a ServerHello, a Retry or a close all mean the server
 * is up. One packet out, no handshake, no certificate check.
 */
object QuicProbe {
    private val initialSalt = hex("38762cf7f55934b34d179ae6a4c80cadccbb7f0a")
    private val random = SecureRandom()

    fun probe(
        address: InetSocketAddress,
        sni: String,
        alpn: List<String>,
        timeoutMs: Int,
        bind: (DatagramSocket) -> Unit = {},
    ): PingResult = try {
        DatagramSocket().use { socket ->
            bind(socket)
            socket.soTimeout = timeoutMs
            socket.connect(address)
            val packet = initialPacket(randomBytes(8), randomBytes(8), sni, alpn)
            val start = System.nanoTime()
            socket.send(DatagramPacket(packet, packet.size))
            val buffer = ByteArray(1500)
            socket.receive(DatagramPacket(buffer, buffer.size))
            PingResult.Ms(maxOf(1, ((System.nanoTime() - start) / 1_000_000).toInt()))
        }
    } catch (e: SocketTimeoutException) {
        PingResult.Failed
    } catch (e: IOException) {
        PingResult.Failed
    }

    class Keys(val key: ByteArray, val iv: ByteArray, val hp: ByteArray)

    /** Client Initial keys for a destination connection ID (RFC 9001 §5.2). */
    fun clientKeys(dcid: ByteArray): Keys {
        val initial = hmac(initialSalt, dcid)
        val client = expandLabel(initial, "client in", 32)
        return Keys(expandLabel(client, "quic key", 16), expandLabel(client, "quic iv", 12), expandLabel(client, "quic hp", 16))
    }

    /** The header protection mask for a 16-byte sample (RFC 9001 §5.4.3). */
    fun headerMask(hp: ByteArray, sample: ByteArray): ByteArray {
        val aes = Cipher.getInstance("AES/ECB/NoPadding")
        aes.init(Cipher.ENCRYPT_MODE, SecretKeySpec(hp, "AES"))
        return aes.doFinal(sample, 0, 16)
    }

    /** A protected Initial packet, padded to the 1200 bytes servers require. */
    fun initialPacket(dcid: ByteArray, scid: ByteArray, sni: String, alpn: List<String>): ByteArray {
        val keys = clientKeys(dcid)
        val pnLength = 4
        val header = ByteArrayOutputStream().apply {
            write(0xC0 or (pnLength - 1)) // long header, Initial
            writeInt(0x00000001)
            write(dcid.size); write(dcid)
            write(scid.size); write(scid)
            write(0) // token length
        }
        val headerSize = header.size() + 2 + pnLength // + 2-byte length + packet number
        val tagSize = 16

        val hello = clientHello(sni, alpn, scid)
        val crypto = ByteArrayOutputStream().apply {
            write(0x06) // CRYPTO frame
            writeVarint(0)
            writeVarint(hello.size.toLong())
            write(hello)
        }.toByteArray()
        val payload = crypto.copyOf(maxOf(crypto.size, 1200 - headerSize - tagSize)) // the rest is PADDING (0x00)

        header.write2ByteVarint(pnLength + payload.size + tagSize)
        header.writeInt(0) // packet number 0
        val aad = header.toByteArray()

        val gcm = Cipher.getInstance("AES/GCM/NoPadding")
        gcm.init(Cipher.ENCRYPT_MODE, SecretKeySpec(keys.key, "AES"), GCMParameterSpec(128, keys.iv)) // nonce = iv xor pn 0
        gcm.updateAAD(aad)
        val sealed = gcm.doFinal(payload)

        val packet = aad + sealed
        val pnOffset = aad.size - pnLength
        val mask = headerMask(keys.hp, packet.copyOfRange(pnOffset + 4, pnOffset + 20))
        packet[0] = (packet[0].toInt() xor (mask[0].toInt() and 0x0f)).toByte()
        for (i in 0 until pnLength) packet[pnOffset + i] = (packet[pnOffset + i].toInt() xor mask[1 + i].toInt()).toByte()
        return packet
    }

    private fun clientHello(sni: String, alpn: List<String>, scid: ByteArray): ByteArray {
        val extensions = ByteArrayOutputStream()
        fun ext(type: Int, body: ByteArray) {
            extensions.writeShort(type); extensions.writeShort(body.size); extensions.write(body)
        }
        if (!ShareLink.isIpAddress(sni) && sni.isNotEmpty()) {
            val name = sni.toByteArray()
            ext(0x0000, bytes { writeShort(name.size + 3); write(0); writeShort(name.size); write(name) })
        }
        ext(0x000a, bytes { writeShort(4); writeShort(0x001d); writeShort(0x0017) }) // x25519, secp256r1
        val signatures = listOf(0x0403, 0x0804, 0x0401, 0x0503, 0x0805, 0x0501, 0x0806, 0x0601, 0x0807)
        ext(0x000d, bytes { writeShort(signatures.size * 2); signatures.forEach { writeShort(it) } })
        val protocols = bytes { alpn.forEach { val p = it.toByteArray(); write(p.size); write(p) } }
        ext(0x0010, bytes { writeShort(protocols.size); write(protocols) })
        ext(0x002b, bytes { write(2); writeShort(0x0304) }) // TLS 1.3 only
        // Any 32 bytes are a valid X25519 public key; the answer is never read.
        ext(0x0033, bytes { writeShort(36); writeShort(0x001d); writeShort(32); write(randomBytes(32)) })
        ext(0x0039, bytes {
            transportParameter(0x01, 30_000) // max_idle_timeout
            transportParameter(0x04, 1 shl 20) // initial_max_data
            transportParameter(0x05, 1 shl 18)
            transportParameter(0x06, 1 shl 18)
            transportParameter(0x07, 1 shl 18)
            transportParameter(0x08, 100) // initial_max_streams_bidi
            transportParameter(0x09, 100)
            writeVarint(0x0f); writeVarint(scid.size.toLong()); write(scid) // initial_source_connection_id
        })

        val body = bytes {
            writeShort(0x0303)
            write(randomBytes(32))
            write(0) // legacy_session_id: empty in QUIC
            writeShort(6); writeShort(0x1301); writeShort(0x1302); writeShort(0x1303)
            write(1); write(0) // compression: null
            writeShort(extensions.size()); write(extensions.toByteArray())
        }
        return bytes { write(0x01); write(body.size ushr 16); writeShort(body.size and 0xffff); write(body) }
    }

    private fun ByteArrayOutputStream.transportParameter(id: Int, value: Long) {
        val encoded = bytes { writeVarint(value) }
        writeVarint(id.toLong()); writeVarint(encoded.size.toLong()); write(encoded)
    }

    private fun ByteArrayOutputStream.transportParameter(id: Int, value: Int) = transportParameter(id, value.toLong())

    private fun hmac(key: ByteArray, data: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(data)
    }

    /** HKDF-Expand-Label with an empty context (RFC 8446 §7.1); one block is enough here. */
    private fun expandLabel(secret: ByteArray, label: String, length: Int): ByteArray {
        val full = "tls13 $label".toByteArray()
        val info = bytes { writeShort(length); write(full.size); write(full); write(0) }
        return hmac(secret, info + byteArrayOf(1)).copyOf(length)
    }

    private fun randomBytes(n: Int) = ByteArray(n).also(random::nextBytes)

    private inline fun bytes(build: ByteArrayOutputStream.() -> Unit): ByteArray =
        ByteArrayOutputStream().apply(build).toByteArray()

    private fun ByteArrayOutputStream.writeShort(v: Int) {
        write(v ushr 8 and 0xff); write(v and 0xff)
    }

    private fun ByteArrayOutputStream.writeInt(v: Int) {
        writeShort(v ushr 16 and 0xffff); writeShort(v and 0xffff)
    }

    private fun ByteArrayOutputStream.write2ByteVarint(v: Int) = writeShort(0x4000 or v)

    private fun ByteArrayOutputStream.writeVarint(v: Long) {
        when {
            v < 0x40 -> write(v.toInt())
            v < 0x4000 -> writeShort(0x4000 or v.toInt())
            v < 0x40000000 -> writeInt(0x80000000.toInt() or v.toInt())
            else -> {
                writeInt((0xC0000000L or (v ushr 32)).toInt()); writeInt(v.toInt())
            }
        }
    }

    internal fun hex(s: String): ByteArray = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}
