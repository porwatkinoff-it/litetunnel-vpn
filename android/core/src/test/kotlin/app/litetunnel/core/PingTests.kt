package app.litetunnel.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.IdentityHashMap
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class PingTests {
    private fun hex(s: String) = QuicProbe.hex(s)

    @Test
    fun quicInitialKeysMatchRfc9001() {
        // RFC 9001 Appendix A.1.
        val keys = QuicProbe.clientKeys(hex("8394c8f03e515708"))
        assertArrayEquals(hex("1f369613dd76d5467730efcbe3b1a22d"), keys.key)
        assertArrayEquals(hex("fa044b2f42a3fd3b46fb255c"), keys.iv)
        assertArrayEquals(hex("9f50449e04a0e810283a1e9933adedd2"), keys.hp)
    }

    @Test
    fun quicHeaderMaskMatchesRfc9001() {
        // RFC 9001 Appendix A.2.
        val keys = QuicProbe.clientKeys(hex("8394c8f03e515708"))
        val mask = QuicProbe.headerMask(keys.hp, hex("d1b1c98dd7689fb8ec11d242b123dc9b"))
        assertArrayEquals(hex("437b9aec36"), mask.copyOf(5))
    }

    @Test
    fun quicInitialPacketDecrypts() {
        val dcid = hex("0102030405060708")
        val scid = hex("1112131415161718")
        val packet = QuicProbe.initialPacket(dcid, scid, "h.example.com", listOf("h3"))
        assertTrue("servers drop Initials under 1200 bytes", packet.size >= 1200)
        assertTrue("long header", packet[0].toInt() and 0x80 != 0)
        assertArrayEquals(byteArrayOf(0, 0, 0, 1), packet.copyOfRange(1, 5))

        // Remove header protection, then open the payload as a server would.
        val keys = QuicProbe.clientKeys(dcid)
        val pnOffset = 1 + 4 + 1 + dcid.size + 1 + scid.size + 1 + 2
        val mask = QuicProbe.headerMask(keys.hp, packet.copyOfRange(pnOffset + 4, pnOffset + 20))
        val header = packet.copyOf(pnOffset + 4)
        header[0] = (header[0].toInt() xor (mask[0].toInt() and 0x0f)).toByte()
        assertEquals("4-byte packet number", 3, header[0].toInt() and 0x03)
        for (i in 0 until 4) header[pnOffset + i] = (header[pnOffset + i].toInt() xor mask[1 + i].toInt()).toByte()
        assertArrayEquals(byteArrayOf(0, 0, 0, 0), header.copyOfRange(pnOffset, pnOffset + 4))
        val length = ((header[pnOffset - 2].toInt() and 0x3f) shl 8) or (header[pnOffset - 1].toInt() and 0xff)
        assertEquals(packet.size - pnOffset, length)

        val gcm = Cipher.getInstance("AES/GCM/NoPadding")
        gcm.init(Cipher.DECRYPT_MODE, SecretKeySpec(keys.key, "AES"), GCMParameterSpec(128, keys.iv))
        gcm.updateAAD(header)
        val payload = gcm.doFinal(packet, header.size, packet.size - header.size)
        assertEquals("CRYPTO frame", 0x06, payload[0].toInt())
        assertEquals("offset 0", 0x00, payload[1].toInt())
        val helloAt = if (payload[2].toInt() and 0xc0 == 0x40) 4 else 3
        assertEquals("ClientHello", 0x01, payload[helloAt].toInt())
        assertTrue("SNI is in the hello", String(payload, Charsets.ISO_8859_1).contains("h.example.com"))
    }

    @Test
    fun tcpPingMeasuresAnOpenPortAndFailsOnAClosedOne() {
        ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { listener ->
            val open = Server(null, ShareLink.parse("trojan://pw@127.0.0.1:${listener.localPort}#T"))
            val result = DefaultPinger.measure(open, 1_000)
            assertTrue("$result", result is PingResult.Ms && result.value >= 1)
        }
        val port = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }
        val closed = Server(null, ShareLink.parse("trojan://pw@127.0.0.1:$port#T"))
        assertEquals(PingResult.Failed, DefaultPinger.measure(closed, 1_000))
    }

    @Test
    fun probesUseTheGivenNetwork() {
        val calls = mutableListOf<String>()
        val network = object : ProbeNetwork {
            override fun resolve(host: String): InetAddress {
                calls += "resolve $host"
                return InetAddress.getLoopbackAddress()
            }

            override fun bind(socket: Socket) {
                calls += "tcp"
            }

            override fun bind(socket: DatagramSocket) {
                calls += "udp"
            }
        }
        ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { listener ->
            val tcp = Server(null, ShareLink.parse("trojan://pw@far.example.com:${listener.localPort}#T"))
            assertTrue(SocketPinger(network).measure(tcp, 1_000) is PingResult.Ms)
        }
        val quic = Server(null, ShareLink.parse("hy2://pw@quic.example.com:9#H"))
        SocketPinger(network).measure(quic, 100)
        assertEquals(listOf("resolve far.example.com", "tcp", "resolve quic.example.com", "udp"), calls)
    }

    @Test
    fun obfuscatedHysteriaIsNotMeasurable() {
        val server = Server(null, ShareLink.parse("hy2://pw@127.0.0.1:443?obfs=salamander&obfs-password=x#H"))
        assertNull(DefaultPinger.measure(server, 1_000))
    }

    @Test
    fun groupTakesItsFastestMember() {
        val members = listOf("a", "b", "c").map { ShareLink.parse("trojan://pw@$it.example.com:443#$it") }
        val group = Server(null, ServerGroup.make(members, "Авто-выбор"))
        val single = Server(null, ShareLink.parse("trojan://pw@d.example.com:443#d"))
        val dead = Server(null, ServerGroup.make(members.take(2), "Мёртвая"))
        val times = mapOf("a.example.com" to 80, "b.example.com" to 30, "d.example.com" to 50)

        val results = ConcurrentHashMap<UUID, Any>()
        Ping.measureAll(listOf(group, single), { s, _ -> times[s.host]?.let { PingResult.Ms(it) } ?: PingResult.Failed }) { id, r ->
            results[id] = r ?: "null"
        }
        assertEquals(PingResult.Ms(30), results[group.id])
        assertEquals(PingResult.Ms(50), results[single.id])

        assertEquals(PingResult.Failed, Ping.measure(dead, { _, _ -> PingResult.Failed }))
        assertNull("a group nobody can measure", Ping.measure(dead, { _, _ -> null }))
    }

    @Test
    fun groupMembersAreTheSameObjectsOnEveryRead() {
        // The proxy ping gives each member a port up front, keyed by the object.
        val members = listOf("a", "b").map { ShareLink.parse("trojan://pw@$it.example.com:443#$it") }
        val group = Server(null, ServerGroup.make(members, "Авто-выбор"))
        val ports = IdentityHashMap<Server, Int>().apply { group.members.forEachIndexed { i, member -> put(member, 1000 + i) } }

        var result: PingResult? = null
        Ping.measureAll(listOf(group), { s, _ -> ports[s]?.let { PingResult.Ms(it) } ?: PingResult.Failed }) { _, r -> result = r }
        assertEquals(PingResult.Ms(1000), result)
    }

    @Test
    fun realModeReadsLikeAPing() {
        assertEquals(PingResult.Ms(100), Ping.result(330, PingMode.REAL))
        assertEquals(PingResult.Ms(1), Ping.result(1, PingMode.REAL))
        assertEquals(PingResult.Ms(330), Ping.result(330, PingMode.PROXY_GET))
        assertEquals(PingResult.Failed, Ping.result(null, PingMode.REAL))
    }

    @Test
    fun fastestPrefersTimeOverFailure() {
        assertEquals(PingResult.Ms(40), Ping.fastest(listOf(PingResult.Failed, PingResult.Ms(80), null, PingResult.Ms(40))))
        assertEquals(PingResult.Failed, Ping.fastest(listOf(null, PingResult.Failed)))
        assertNull(Ping.fastest(listOf(null)))
    }

    @Test
    fun testUrlTakesOnlyWebAddresses() {
        assertEquals("https://cp.example.com/x", Ping.testUrl(" https://cp.example.com/x ").toString())
        assertEquals("http://1.1.1.1", Ping.testUrl("http://1.1.1.1").toString())
        for (bad in listOf("", "cp.example.com", "ftp://cp.example.com/", "http://", "http://bad host/")) {
            assertEquals(bad, AppSettings.DEFAULT_PING_URL, Ping.testUrl(bad).toString())
        }
    }

    /** A one-shot HTTP proxy on 127.0.0.1 that answers with `status`. */
    private fun fakeProxy(status: Int, onRequest: (String) -> Unit = {}): ServerSocket {
        val listener = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        Thread {
            runCatching {
                listener.accept().use { socket ->
                    val reader = socket.getInputStream().bufferedReader()
                    onRequest(reader.readLine())
                    while (reader.readLine()?.isNotEmpty() == true) Unit
                    socket.getOutputStream().write("HTTP/1.1 $status X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray())
                }
            }
        }.apply { isDaemon = true }.start()
        return listener
    }

    @Test
    fun requestGoesThroughTheProxy() {
        var line = ""
        fakeProxy(204) { line = it }.use { proxy ->
            val ms = Ping.request(Ping.testUrl(AppSettings.DEFAULT_PING_URL), proxy.localPort, head = true)
            assertTrue("answered: $ms", ms != null && ms >= 1)
        }
        assertEquals("HEAD ${AppSettings.DEFAULT_PING_URL} HTTP/1.1", line)

        fakeProxy(502).use { proxy ->
            assertNull("an error status is a failure", Ping.request(Ping.testUrl(""), proxy.localPort, head = false))
        }
        val closed = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }
        assertNull(Ping.request(Ping.testUrl(""), closed, head = false, timeoutMs = 1_000))
    }
}
