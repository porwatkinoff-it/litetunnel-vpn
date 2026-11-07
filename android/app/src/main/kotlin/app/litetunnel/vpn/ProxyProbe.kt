package app.litetunnel.vpn

import android.content.Context
import android.net.NetworkCapabilities
import app.litetunnel.core.AppSettings
import app.litetunnel.core.ConfigBuilder
import app.litetunnel.core.Ping
import app.litetunnel.core.PingMode
import app.litetunnel.core.PingResult
import app.litetunnel.core.Pinger
import app.litetunnel.core.Server
import io.nekohasekai.libbox.CommandServer
import io.nekohasekai.libbox.CommandServerHandler
import io.nekohasekai.libbox.InterfaceUpdateListener
import io.nekohasekai.libbox.Libbox
import io.nekohasekai.libbox.OverrideOptions
import io.nekohasekai.libbox.SystemProxyStatus
import io.nekohasekai.libbox.TunOptions
import java.net.ServerSocket
import java.util.Collections
import java.util.IdentityHashMap
import java.util.UUID

/**
 * Checks servers through themselves, in every ping mode: a second core,
 * alive only while the pings run, with a local proxy on 127.0.0.1 for each
 * server. A request to the test address and a 64 KB download go through
 * each one (Ping.throughServer); only a server that passes both gets a time.
 * The core's sockets skip the tunnel, so the time is the server's, not the
 * tunnel's.
 */
internal object ProxyProbe {
    /**
     * Blocks until every server is measured. A link the core cannot run
     * fails on its own. Throws when the core does not start for the rest
     * either; then nothing has been reported.
     */
    fun measureAll(context: Context, servers: List<Server>, settings: AppSettings, onResult: (UUID, PingResult?) -> Unit) {
        val targets = servers.flatMap { it.members.ifEmpty { listOf(it) } }
        if (targets.isEmpty()) return
        Core.setup(context)
        val running = try {
            Running.start(context, targets, settings)
        } catch (e: Exception) {
            // One link the core cannot run stops the whole config.
            val broken = broken(targets, settings)
            if (broken.isEmpty()) throw e
            val rest = targets.filterNot { it in broken }
            if (rest.isEmpty()) null else Running.start(context, rest, settings)
        }
        try {
            val url = Ping.testUrl(settings.pingUrl)
            val mode = settings.pingMode
            val direct = when (mode) {
                PingMode.TCP -> Tunnel.pinger()
                PingMode.ICMP -> Tunnel.icmpPinger()
                PingMode.REAL, PingMode.PROXY_GET, PingMode.PROXY_HEAD -> null
            }
            val pinger = Pinger { target, _ ->
                val port = running?.ports?.get(target) ?: return@Pinger PingResult.Failed
                Ping.throughServer(target, port, url, mode, direct)
            }
            Ping.measureAll(servers, pinger, timeoutMs = Ping.REQUEST_TIMEOUT_MS, onResult = onResult)
        } finally {
            running?.close()
        }
    }

    /**
     * The targets the core cannot run, each checked alone. None when a
     * config without servers fails as well: then the cause is not a link.
     */
    private fun broken(targets: List<Server>, settings: AppSettings): Set<Server> {
        val port = freePort()
        fun runs(list: List<Server>): Boolean = try {
            Libbox.checkConfig(ConfigBuilder.probe(list, list.map { port }, settings).jsonString())
            true
        } catch (e: Exception) {
            false
        }
        if (!runs(emptyList())) return emptySet()
        return targets.filterTo(Collections.newSetFromMap(IdentityHashMap())) { !runs(listOf(it)) }
    }

    /** The probe core and the port of each target's proxy, keyed by the object. */
    private class Running(private val core: CommandServer, val ports: IdentityHashMap<Server, Int>) : AutoCloseable {
        override fun close() {
            runCatching { core.closeService() }
            runCatching { core.close() }
        }

        companion object {
            fun start(context: Context, targets: List<Server>, settings: AppSettings): Running {
                val ports = targets.map { freePort() }
                val config = ConfigBuilder.probe(targets, ports, settings).jsonString()
                // No start(): it opens the command socket, and that one is the tunnel's.
                val core = CommandServer(Handler, ProbePlatform(context))
                try {
                    core.startOrReloadService(config, OverrideOptions())
                } catch (e: Exception) {
                    runCatching { core.closeService() }
                    runCatching { core.close() }
                    throw e
                }
                val byTarget = IdentityHashMap<Server, Int>()
                targets.zip(ports).forEach { (target, port) -> byTarget[target] = port }
                return Running(core, byTarget)
            }
        }
    }

    /** A port nothing listens on now; the core takes it a moment later. */
    private fun freePort(): Int = ServerSocket(0, 0, Ping.LOOPBACK).use { it.localPort }

    private object Handler : CommandServerHandler {
        override fun serviceStop() = Unit

        override fun serviceReload() = Unit

        override fun getSystemProxyStatus() = SystemProxyStatus()

        override fun setSystemProxyEnabled(enabled: Boolean) = Unit

        override fun writeDebugMessage(message: String) = Unit

        override fun connectSSHAgent(): Int = throw Exception("Не поддерживается")

        override fun triggerNativeCrash() = throw Exception("Не поддерживается")
    }
}

/**
 * The probe core's view of Android: no TUN, and the physical network told
 * once, since the core lives for a few seconds. While the tunnel is up its
 * sockets are protected from it, as the tunnel's own are.
 */
private class ProbePlatform(context: Context) : CorePlatform(context) {
    override fun openTun(options: TunOptions): Int = unsupported()

    override fun autoDetectInterfaceControl(fd: Int) {
        val status = Tunnel.state.value.status
        if (status == TunnelStatus.STOPPED) return
        val protected = LiteVpnService.current?.protect(fd) == true
        // Through the tunnel the time would be the selected server's too.
        if (!protected && status == TunnelStatus.CONNECTED) throw Exception("Не удалось вывести сокет мимо VPN")
    }

    override fun startDefaultInterfaceMonitor(listener: InterfaceUpdateListener) {
        val network = Tunnel.physicalNetwork ?: physicalDefault(connectivity) ?: return
        val name = connectivity.getLinkProperties(network)?.interfaceName ?: return
        val index = runCatching { java.net.NetworkInterface.getByName(name)?.index }.getOrNull() ?: return
        val expensive = connectivity.getNetworkCapabilities(network)
            ?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) != true
        listener.updateDefaultInterface(name, index, expensive, false)
    }

    override fun closeDefaultInterfaceMonitor(listener: InterfaceUpdateListener) = Unit
}
