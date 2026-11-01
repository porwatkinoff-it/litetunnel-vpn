package app.litetunnel.vpn

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.VpnService
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.ParcelFileDescriptor
import io.nekohasekai.libbox.ConnectionOwner
import io.nekohasekai.libbox.InterfaceUpdateListener
import io.nekohasekai.libbox.Libbox
import io.nekohasekai.libbox.LocalDNSTransport
import io.nekohasekai.libbox.NeighborUpdateListener
import io.nekohasekai.libbox.NetworkInterfaceIterator
import io.nekohasekai.libbox.Notification
import io.nekohasekai.libbox.PlatformInterface
import io.nekohasekai.libbox.PlatformUser
import io.nekohasekai.libbox.ShellSession
import io.nekohasekai.libbox.StringIterator
import io.nekohasekai.libbox.TunOptions
import io.nekohasekai.libbox.WIFIState
import io.nekohasekai.libbox.BridgeOptions
import io.nekohasekai.libbox.BridgeSession
import java.net.Inet6Address
import io.nekohasekai.libbox.NetworkInterface as BoxInterface

/**
 * What the tunnel's core asks of Android: the TUN, socket protection and the
 * physical network.
 */
internal class AndroidPlatform(private val service: VpnService) : CorePlatform(service) {
    private var tun: ParcelFileDescriptor? = null
    private var monitor: NetworkMonitor? = null

    override fun openTun(options: TunOptions): Int {
        if (VpnService.prepare(service) != null) throw Exception("Нет разрешения на VPN")
        val builder = service.Builder().setSession("LiteTunnel").setMtu(options.getMTU())
        if (Build.VERSION.SDK_INT >= 29) {
            // Metered then follows the real network, not "always metered".
            builder.setMetered(false)
        }

        val inet4 = options.getInet4Address().toList()
        val inet6 = options.getInet6Address().toList()
        for (prefix in inet4 + inet6) builder.addAddress(prefix.address(), prefix.prefix())

        if (options.getAutoRoute()) {
            for (dns in options.getDNSServerAddress().toList()) builder.addDnsServer(dns)
            // The core has already cut the excluded ranges out of these.
            val routes4 = options.getInet4RouteRange().toList()
            if (routes4.isNotEmpty()) routes4.forEach { builder.addRoute(it.address(), it.prefix()) }
            else if (inet4.isNotEmpty()) builder.addRoute("0.0.0.0", 0)
            // No IPv6 address means Android blocks IPv6 for the apps inside: no leak.
            val routes6 = options.getInet6RouteRange().toList()
            if (routes6.isNotEmpty()) routes6.forEach { builder.addRoute(it.address(), it.prefix()) }
            else if (inet6.isNotEmpty()) builder.addRoute("::", 0)

            val include = options.getIncludePackage().toList()
            if (include.isNotEmpty()) {
                for (name in include) runCatching { builder.addAllowedApplication(name) }
            } else {
                // This app stays inside. The system and mixed stacks answer TCP
                // from a socket of this app, and outside the VPN those answers
                // never reach the tunnel. Pings bind to the physical network.
                for (name in options.getExcludePackage().toList()) {
                    runCatching { builder.addDisallowedApplication(name) }
                }
            }
        }

        val pfd = builder.establish()
            ?: throw Exception("Android не дал создать VPN: нет разрешения или его забрало другое приложение")
        // The core dups the descriptor; the one we hold is ours to close.
        val old = tun
        tun = pfd
        old?.close()
        return pfd.fd
    }

    fun close() {
        monitor?.close()
        monitor = null
        tun?.close()
        tun = null
    }

    override fun autoDetectInterfaceControl(fd: Int) {
        if (!service.protect(fd)) throw Exception("Не удалось вывести сокет ядра мимо VPN")
    }

    override fun startDefaultInterfaceMonitor(listener: InterfaceUpdateListener) {
        monitor?.close()
        monitor = NetworkMonitor(service, connectivity, listener).also { it.start() }
    }

    override fun closeDefaultInterfaceMonitor(listener: InterfaceUpdateListener) {
        monitor?.close()
        monitor = null
    }
}

/**
 * What every core asks of Android: the physical networks. Everything else
 * (shell, SSH, process lookup) is off.
 */
internal abstract class CorePlatform(context: Context) : PlatformInterface {
    protected val connectivity: ConnectivityManager = context.getSystemService(ConnectivityManager::class.java)

    override fun usePlatformAutoDetectInterfaceControl() = true

    override fun getInterfaces(): NetworkInterfaceIterator {
        val list = ArrayList<BoxInterface>()
        for (network in connectivity.allNetworks) {
            val caps = connectivity.getNetworkCapabilities(network) ?: continue
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) continue
            val link = connectivity.getLinkProperties(network) ?: continue
            val name = link.interfaceName ?: continue
            val system = runCatching { java.net.NetworkInterface.getByName(name) }.getOrNull() ?: continue
            list.add(BoxInterface().apply {
                setName(name)
                setIndex(system.index)
                runCatching { setMTU(system.mtu) }
                setAddresses(StringList(link.linkAddresses.map { address ->
                    // Go's prefix parser rejects a "%zone", so drop the scope.
                    val ip = address.address.let { if (it is Inet6Address) Inet6Address.getByAddress(it.address) else it }
                    "${ip.hostAddress}/${address.prefixLength}"
                }))
                var flags = 0
                if (caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) flags = flags or IFF_UP or IFF_RUNNING
                if (system.isLoopback) flags = flags or IFF_LOOPBACK
                if (system.isPointToPoint) flags = flags or IFF_POINTOPOINT
                if (system.supportsMulticast()) flags = flags or IFF_MULTICAST
                setFlags(flags)
                setType(typeOf(caps))
                setDNSServer(StringList(link.dnsServers.mapNotNull { it.hostAddress }))
                setMetered(!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED))
            })
        }
        return InterfaceList(list)
    }

    override fun underNetworkExtension() = false

    override fun includeAllNetworks() = false

    override fun clearDNSCache() = Unit

    override fun readWIFIState(): WIFIState = WIFIState("", "")

    override fun localDNSTransport(): LocalDNSTransport? = null

    override fun registerMyInterface(name: String) = Unit

    override fun tailscaleHostname() = ""

    override fun sendNotification(notification: Notification) = Unit

    override fun cancelNotification(identifier: String, typeID: Int) = Unit

    override fun startNeighborMonitor(listener: NeighborUpdateListener) = Unit

    override fun closeNeighborMonitor(listener: NeighborUpdateListener) = Unit

    override fun findConnectionOwner(
        ipProtocol: Int,
        sourceAddress: String,
        sourcePort: Int,
        destinationAddress: String,
        destinationPort: Int,
    ): ConnectionOwner = unsupported()

    override fun usePlatformBridge() = false

    override fun createBridge(options: BridgeOptions): BridgeSession = unsupported()

    override fun usePlatformShell() = false

    override fun checkPlatformShell(): Unit = unsupported()

    override fun lookupUser(name: String): PlatformUser = unsupported()

    override fun openShellSession(
        user: PlatformUser,
        command: String,
        args: StringIterator,
        term: String,
        rows: Int,
        cols: Int,
    ): ShellSession = unsupported()

    override fun readSystemSSHHostKey(): String = unsupported()

    override fun lookupSFTPServer(): String = unsupported()

    protected fun unsupported(): Nothing = throw Exception("Не поддерживается")

    private fun typeOf(caps: NetworkCapabilities) = when {
        caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> Libbox.InterfaceTypeWIFI
        caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> Libbox.InterfaceTypeCellular
        caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> Libbox.InterfaceTypeEthernet
        else -> Libbox.InterfaceTypeOther
    }

    private companion object {
        // <net/if.h>
        const val IFF_UP = 0x1
        const val IFF_LOOPBACK = 0x8
        const val IFF_POINTOPOINT = 0x10
        const val IFF_RUNNING = 0x40
        const val IFF_MULTICAST = 0x1000
    }
}

/**
 * Tells the core which physical network is the default one. This app is
 * inside its own VPN, so its default network is the tunnel; it follows the
 * best network without NOT_VPN instead. That network is the system default
 * already, so the request keeps no other radio awake.
 */
private class NetworkMonitor(
    private val service: VpnService,
    private val connectivity: ConnectivityManager,
    private val listener: InterfaceUpdateListener,
) : ConnectivityManager.NetworkCallback() {
    private val thread = HandlerThread("network").apply { start() }
    private val handler = Handler(thread.looper)
    private var current: Network? = null
    private var last: Pair<String, Boolean>? = null

    fun start() {
        // The core waits for the first answer before it routes anything. On a
        // reload the default network is the old tunnel, so look further.
        physicalDefault(connectivity)?.let { update(it, null, null) }
        // A default request has NOT_VPN.
        val request = NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)
            .build()
        if (Build.VERSION.SDK_INT >= 31) {
            connectivity.registerBestMatchingNetworkCallback(request, this, handler)
        } else {
            connectivity.requestNetwork(request, this, handler)
        }
    }

    @Synchronized
    fun close() {
        runCatching { connectivity.unregisterNetworkCallback(this) }
        thread.quitSafely()
        if (Tunnel.physicalNetwork == current) Tunnel.physicalNetwork = null
        current = null
    }

    override fun onAvailable(network: Network) = update(network, null, null)

    override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) = update(network, caps, null)

    override fun onLinkPropertiesChanged(network: Network, link: LinkProperties) = update(network, null, link)

    @Synchronized
    override fun onLost(network: Network) {
        if (network != current) return
        current = null
        last = null
        Tunnel.physicalNetwork = null
        runCatching { service.setUnderlyingNetworks(null) }
        listener.updateDefaultInterface("", -1, false, false)
    }

    @Synchronized
    private fun update(network: Network, knownCaps: NetworkCapabilities?, knownLink: LinkProperties?) {
        val caps = knownCaps ?: connectivity.getNetworkCapabilities(network) ?: return
        if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) return
        val link = knownLink ?: connectivity.getLinkProperties(network) ?: return
        val name = link.interfaceName ?: return
        val expensive = !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
        if (network != current) {
            current = network
            Tunnel.physicalNetwork = network
            runCatching { service.setUnderlyingNetworks(arrayOf(network)) }
        }
        if (last == name to expensive) return
        last = name to expensive
        val index = runCatching { java.net.NetworkInterface.getByName(name)?.index }.getOrNull() ?: return
        listener.updateDefaultInterface(name, index, expensive, false)
    }
}

/** The default network when it is not a VPN, else the best physical one. */
internal fun physicalDefault(connectivity: ConnectivityManager): Network? {
    fun isPhysical(network: Network): Boolean {
        val caps = connectivity.getNetworkCapabilities(network) ?: return false
        return !caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) &&
            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
    }
    return connectivity.activeNetwork?.takeIf(::isPhysical)
        ?: connectivity.allNetworks.filter(::isPhysical).maxByOrNull { network ->
            connectivity.getNetworkCapabilities(network)?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) == true
        }
}
