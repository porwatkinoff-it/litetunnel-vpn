package app.litetunnel.core

import java.io.IOException
import java.net.DatagramSocket
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.Socket
import java.net.URI
import java.net.URL
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * Measures one server, outside the tunnel. Blocking. Replace it to change
 * how ping works; Ping.measureAll takes any implementation.
 *
 * Returns null when the server cannot be measured this way.
 */
fun interface Pinger {
    fun measure(server: Server, timeoutMs: Int): PingResult?
}

/**
 * Which network the probes use. The default leaves it to the system. While
 * the tunnel is up, Android passes its physical network instead: through the
 * tunnel every handshake would end at the core and take about 1 ms.
 */
interface ProbeNetwork {
    fun resolve(host: String): InetAddress = InetAddress.getByName(host)

    /** Called before the socket connects. */
    fun bind(socket: Socket) = Unit

    /** Called before the socket connects. */
    fun bind(socket: DatagramSocket) = Unit

    object Default : ProbeNetwork
}

/**
 * TCP servers get a TCP handshake. Hysteria2 and TUIC run over QUIC, so they
 * get a QUIC Initial instead and any answer counts; with salamander obfs the
 * server does not answer plain QUIC, and the result is null (not measurable).
 *
 * The name is resolved first and only the handshake is timed, so the number
 * is the round trip to the server, not to the DNS resolver.
 */
open class SocketPinger(private val network: ProbeNetwork) : Pinger {
    override fun measure(server: Server, timeoutMs: Int): PingResult? {
        val quic = server.proto == ProxyProtocol.HYSTERIA2 || server.proto == ProxyProtocol.TUIC
        if (quic && server.outbound["obfs"] != null) return null
        if (!ShareLink.validPort(server.port)) return PingResult.Failed
        val address = try {
            network.resolve(server.host)
        } catch (e: IOException) {
            return PingResult.Failed
        }
        return if (quic) {
            val tls = server.outbound["tls"]
            val alpn = tls?.get("alpn")?.array?.mapNotNull { it.string }.orEmpty().ifEmpty { listOf("h3") }
            val sni = tls?.get("server_name")?.string ?: server.host
            QuicProbe.probe(InetSocketAddress(address, server.port), sni, alpn, timeoutMs, network::bind)
        } else {
            tcp(InetSocketAddress(address, server.port), timeoutMs)
        }
    }

    private fun tcp(address: InetSocketAddress, timeoutMs: Int): PingResult = try {
        Socket().use { socket ->
            network.bind(socket)
            socket.tcpNoDelay = true
            val start = System.nanoTime()
            socket.connect(address, timeoutMs)
            PingResult.Ms(maxOf(1, ((System.nanoTime() - start) / 1_000_000).toInt()))
        }
    } catch (e: IOException) {
        PingResult.Failed
    }
}

object DefaultPinger : SocketPinger(ProbeNetwork.Default)

object Ping {
    const val TIMEOUT_MS = 3_000

    /** Requests through the server get longer: the whole request has to go through. */
    const val REQUEST_TIMEOUT_MS = 5_000

    /**
     * Where the probe core listens. Not getLoopbackAddress(): on Android that
     * is ::1, and the core's inbounds are on 127.0.0.1.
     */
    val LOOPBACK: InetAddress = InetAddress.getByAddress("localhost", byteArrayOf(127, 0, 0, 1))

    fun fastest(results: Collection<PingResult?>): PingResult? = results.filterNotNull().minByOrNull { it.sortValue }

    /**
     * Times one request to `url` through the HTTP proxy on 127.0.0.1:`port`,
     * up to the response headers. Null when it fails or the server answers
     * with an error.
     */
    fun request(url: URL, port: Int, head: Boolean, timeoutMs: Int = REQUEST_TIMEOUT_MS): Int? {
        val proxy = Proxy(Proxy.Type.HTTP, InetSocketAddress(LOOPBACK, port))
        var connection: HttpURLConnection? = null
        return try {
            connection = url.openConnection(proxy) as HttpURLConnection
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.useCaches = false
            connection.instanceFollowRedirects = false
            connection.requestMethod = if (head) "HEAD" else "GET"
            connection.setRequestProperty("Connection", "close")
            val start = System.nanoTime()
            val status = connection.responseCode
            val ms = ((System.nanoTime() - start) / 1_000_000).toInt()
            if (status in 200..399) maxOf(1, ms) else null
        } catch (e: IOException) {
            null
        } finally {
            connection?.disconnect()
        }
    }

    /** What a proxied request time shows as in `mode`. */
    fun result(ms: Int?, mode: PingMode): PingResult {
        if (ms == null) return PingResult.Failed
        if (mode != PingMode.REAL) return PingResult.Ms(ms)
        return PingResult.Ms(maxOf(1, Math.round(ms / 3.3).toInt()))
    }

    /** The address from settings when it is a web one, else the default. */
    fun testUrl(text: String): URL {
        val uri = try {
            URI(text.trim())
        } catch (e: Exception) {
            null
        }
        val scheme = uri?.scheme?.lowercase()
        if (uri != null && (scheme == "http" || scheme == "https") && !uri.host.isNullOrEmpty()) {
            try {
                return uri.toURL()
            } catch (e: Exception) {
                // falls through to the default
            }
        }
        return URL(AppSettings.DEFAULT_PING_URL)
    }

    /** A group is as fast as its fastest member: the one the core picks. */
    fun measure(server: Server, pinger: Pinger = DefaultPinger, timeoutMs: Int = TIMEOUT_MS): PingResult? {
        var result: PingResult? = null
        measureAll(listOf(server), pinger, timeoutMs = timeoutMs) { _, r -> result = r }
        return result
    }

    /**
     * Pings many servers, at most `concurrency` probes at a time, and blocks
     * until all are done. Group members are probes of their own, so a group
     * waits for none of the slots. `onResult` is called once per server, from
     * a worker thread.
     */
    fun measureAll(
        servers: List<Server>,
        pinger: Pinger = DefaultPinger,
        concurrency: Int = 8,
        timeoutMs: Int = TIMEOUT_MS,
        onResult: (UUID, PingResult?) -> Unit,
    ) {
        if (servers.isEmpty()) return
        val pool = Executors.newFixedThreadPool(minOf(concurrency, servers.size * 2).coerceAtLeast(1)) { task ->
            Thread(task, "ping").apply { isDaemon = true }
        }
        try {
            for (server in servers) {
                val targets = server.members.ifEmpty { listOf(server) }
                val left = AtomicInteger(targets.size)
                val results = ConcurrentHashMap<Int, PingResult>()
                targets.forEachIndexed { index, target ->
                    pool.execute {
                        pinger.measure(target, timeoutMs)?.let { results[index] = it }
                        if (left.decrementAndGet() == 0) {
                            onResult(server.id, fastest(results.values))
                        }
                    }
                }
            }
            pool.shutdown()
            while (!pool.awaitTermination(1, TimeUnit.MINUTES)) Unit
        } catch (e: InterruptedException) {
            pool.shutdownNow()
            Thread.currentThread().interrupt()
        }
    }
}
