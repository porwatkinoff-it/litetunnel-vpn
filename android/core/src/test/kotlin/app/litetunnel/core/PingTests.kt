package app.litetunnel.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URL
import java.util.Collections
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

    @Test
    fun generate204NeedsA204() {
        val url = Ping.testUrl("http://check.invalid/generate_204")
        FakeProxy { FakeProxy.Reply("200 OK", "ok".toByteArray()) }.use { stub ->
            assertNull("a page instead of 204 is not the test address", Ping.request(url, stub.port, head = false, timeoutMs = 3_000))
        }
        FakeProxy { FakeProxy.Reply("204 No Content") }.use { real ->
            assertTrue(Ping.request(url, real.port, head = false, timeoutMs = 3_000) != null)
        }
    }

    @Test
    fun otherAddressesPassWith200To399() {
        val url = URL("https://example.com/")
        assertTrue(Ping.passes(200, url))
        assertTrue(Ping.passes(302, url))
        assertFalse(Ping.passes(404, url))
        assertFalse(Ping.passes(500, url))
        val generate = URL("https://www.gstatic.com/generate_204")
        assertTrue(Ping.passes(204, generate))
        assertFalse(Ping.passes(200, generate))
        assertFalse(Ping.passes(302, generate))
    }

    @Test
    fun checkPassesOnTheWholeDownload() {
        var head = ""
        FakeProxy {
            head = it
            FakeProxy.Reply("206 Partial Content", ByteArray(Ping.CHECK_BYTES))
        }.use { proxy ->
            assertTrue(Ping.check(proxy.port, file, idleMs = 1_000, totalMs = 3_000))
        }
        assertTrue(head, head.lowercase().contains("range: bytes=0-${Ping.CHECK_BYTES - 1}"))
    }

    @Test
    fun checkFailsWhenTheConnectionStalls() {
        // About what some networks let through before they freeze a connection.
        FakeProxy { FakeProxy.Reply("200 OK", ByteArray(16 * 1024), length = Ping.CHECK_BYTES, keepOpen = true) }.use { proxy ->
            val start = System.nanoTime()
            assertFalse(Ping.check(proxy.port, file, idleMs = 1_000, totalMs = 3_000))
            assertTrue((System.nanoTime() - start) / 1_000_000 < 5_000)
        }
    }

    @Test
    fun checkEndsAtItsTotalTime() {
        // Never idle long enough to time out a read, but too slow in all.
        FakeProxy { FakeProxy.Reply("200 OK", ByteArray(0), length = Ping.CHECK_BYTES, keepOpen = true, trickle = true) }.use { proxy ->
            val start = System.nanoTime()
            assertFalse(Ping.check(proxy.port, file, idleMs = 1_000, totalMs = 1_500))
            val took = (System.nanoTime() - start) / 1_000_000
            assertTrue("took $took ms", took < 4_000)
        }
    }

    @Test
    fun checkFailsOnAnErrorPage() {
        FakeProxy { FakeProxy.Reply("403 Forbidden", ByteArray(Ping.CHECK_BYTES)) }.use { proxy ->
            assertFalse(Ping.check(proxy.port, file, idleMs = 1_000, totalMs = 3_000))
        }
    }

    @Test
    fun checkStopsWhenTheRangeIsIgnored() {
        FakeProxy { FakeProxy.Reply("200 OK", ByteArray(4 * Ping.CHECK_BYTES)) }.use { proxy ->
            assertTrue(Ping.check(proxy.port, file, idleMs = 1_000, totalMs = 3_000))
        }
    }

    @Test
    fun aTimeMeansTheServerPassedTheCheck() {
        val server = Server(null, ShareLink.parse("trojan://pw@a.example.com:443#a"))
        val url = Ping.testUrl("http://check.invalid/generate_204")
        val answers = Pinger { _, _ -> PingResult.Ms(42) }
        val silent = Pinger { _, _ -> null }
        val works: (String) -> FakeProxy.Reply = { head ->
            if (head.contains("generate_204")) FakeProxy.Reply("204 No Content")
            else FakeProxy.Reply("206 Partial Content", ByteArray(Ping.CHECK_BYTES))
        }
        FakeProxy(works).use { proxy ->
            assertEquals(PingResult.Ms(42), Ping.throughServer(server, proxy.port, url, PingMode.TCP, answers, file, 1_000))
            val real = Ping.throughServer(server, proxy.port, url, PingMode.TCP, silent, file, 1_000)
            assertTrue("$real", real is PingResult.Ms)
            assertTrue(Ping.throughServer(server, proxy.port, url, PingMode.PROXY_GET, null, file, 1_000) is PingResult.Ms)
        }
        // Answers TCP and the short request, then stalls: no time.
        val stalls: (String) -> FakeProxy.Reply = { head ->
            if (head.contains("generate_204")) FakeProxy.Reply("204 No Content")
            else FakeProxy.Reply("200 OK", ByteArray(16 * 1024), length = Ping.CHECK_BYTES, keepOpen = true)
        }
        FakeProxy(stalls).use { proxy ->
            assertEquals(PingResult.Failed, Ping.throughServer(server, proxy.port, url, PingMode.TCP, answers, file, 1_000))
            assertEquals(PingResult.Failed, Ping.throughServer(server, proxy.port, url, PingMode.REAL, null, file, 1_000))
        }
        // A stub page in place of 204: no time either.
        FakeProxy { FakeProxy.Reply("200 OK", ByteArray(Ping.CHECK_BYTES)) }.use { proxy ->
            assertEquals(PingResult.Failed, Ping.throughServer(server, proxy.port, url, PingMode.ICMP, answers, file, 1_000))
        }
    }

    private val file = URL("http://check.invalid/file.bin")
}

/** Answers every proxied request with `answer(head)`, on 127.0.0.1, until closed. */
private class FakeProxy(private val answer: (String) -> Reply) : AutoCloseable {
    class Reply(
        val status: String,
        val body: ByteArray = ByteArray(0),
        /** The Content-Length sent, when it is not the body's. */
        val length: Int = body.size,
        /** Leaves the connection open after the body, as a stalled one. */
        val keepOpen: Boolean = false,
        /** After the body, one byte every 200 ms. */
        val trickle: Boolean = false,
    )

    private val listener = ServerSocket(0, 8, Ping.LOOPBACK)
    private val sockets = Collections.synchronizedList(ArrayList<Socket>())
    val port: Int get() = listener.localPort

    init {
        daemon {
            while (true) {
                val socket = runCatching { listener.accept() }.getOrNull() ?: break
                sockets.add(socket)
                daemon { runCatching { serve(socket) } }
            }
        }
    }

    private fun serve(socket: Socket) {
        val reader = socket.getInputStream().bufferedReader()
        val head = generateSequence { reader.readLine()?.takeIf { it.isNotEmpty() } }.joinToString("\n")
        val reply = answer(head)
        val out = socket.getOutputStream()
        out.write("HTTP/1.1 ${reply.status}\r\nContent-Length: ${reply.length}\r\nConnection: close\r\n\r\n".toByteArray())
        out.write(reply.body)
        out.flush()
        if (reply.trickle) {
            repeat(reply.length - reply.body.size) {
                Thread.sleep(200)
                out.write(0)
                out.flush()
            }
        }
        if (!reply.keepOpen) socket.close()
    }

    override fun close() {
        listener.close()
        sockets.forEach { runCatching { it.close() } }
    }

    private fun daemon(block: () -> Unit) = Thread(block).apply { isDaemon = true }.start()
}
