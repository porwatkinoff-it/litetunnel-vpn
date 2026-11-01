package app.litetunnel.vpn

import android.net.Network
import app.litetunnel.core.DefaultPinger
import app.litetunnel.core.PingResult
import app.litetunnel.core.Pinger
import app.litetunnel.core.ProbeNetwork
import app.litetunnel.core.SocketPinger
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.Socket
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

enum class TunnelStatus { STOPPED, CONNECTING, CONNECTED, STOPPING }

data class TunnelState(
    val status: TunnelStatus = TunnelStatus.STOPPED,
    /** Wall-clock millis of the connect. The elapsed time ticks only while the home screen is visible. */
    val since: Long? = null,
    val serverName: String? = null,
) {
    val isActive: Boolean get() = status == TunnelStatus.CONNECTING || status == TunnelStatus.CONNECTED
}

/**
 * What the service tells the UI. Both live in one process, so a StateFlow is
 * enough: no binder, no polling.
 */
object Tunnel {
    private val _state = MutableStateFlow(TunnelState())
    val state: StateFlow<TunnelState> = _state.asStateFlow()

    private val _errors = MutableSharedFlow<String>(extraBufferCapacity = 4)

    /** Why a start or a reload failed. Lost if no screen is open, like a toast. */
    val errors: SharedFlow<String> = _errors.asSharedFlow()

    /**
     * The physical network under the tunnel while it runs. This app is inside
     * its own VPN, so probes that must skip the tunnel bind to this one.
     */
    @Volatile
    var physicalNetwork: Network? = null
        internal set

    /**
     * Pings go past the tunnel: through it every handshake ends at the core.
     * While it starts or stops there may be no physical network yet, and then
     * they fail rather than show a false 1 ms.
     */
    fun pinger(): Pinger {
        val network = physicalNetwork
        return when {
            network != null -> SocketPinger(object : ProbeNetwork {
                override fun resolve(host: String): InetAddress = network.getByName(host)
                override fun bind(socket: Socket) = network.bindSocket(socket)
                override fun bind(socket: DatagramSocket) = network.bindSocket(socket)
            })
            state.value.status == TunnelStatus.STOPPED -> DefaultPinger
            else -> Pinger { _, _ -> PingResult.Failed }
        }
    }

    /** As [pinger], with an ICMP echo in place of the handshake. */
    fun icmpPinger(): Pinger {
        val network = physicalNetwork
        return when {
            network != null -> IcmpPinger(network)
            state.value.status == TunnelStatus.STOPPED -> IcmpPinger(null)
            else -> Pinger { _, _ -> PingResult.Failed }
        }
    }

    internal fun connecting() = _state.update { it.copy(status = TunnelStatus.CONNECTING) }

    internal fun connected(serverName: String?) = _state.update {
        TunnelState(TunnelStatus.CONNECTED, it.since ?: System.currentTimeMillis(), serverName)
    }

    internal fun stopping() = _state.update { it.copy(status = TunnelStatus.STOPPING) }

    internal fun stopped() {
        _state.value = TunnelState()
    }

    internal fun fail(message: String) {
        _errors.tryEmit(message)
    }
}
