package app.litetunnel.vpn

import android.net.Network
import android.system.ErrnoException
import android.system.Os
import android.system.OsConstants
import android.system.StructPollfd
import app.litetunnel.core.PingResult
import app.litetunnel.core.Pinger
import app.litetunnel.core.Server
import java.io.FileDescriptor
import java.io.IOException
import java.net.Inet6Address
import java.net.InetAddress

/**
 * An ICMP echo to the server's address from a ping socket, which Android
 * gives every app without root. The kernel sets the identifier and the
 * checksum and passes this socket only its own replies.
 *
 * `network` is the physical one while the tunnel is up, null when it is off.
 */
internal class IcmpPinger(private val network: Network?) : Pinger {
    override fun measure(server: Server, timeoutMs: Int): PingResult {
        val address = try {
            network?.getByName(server.host) ?: InetAddress.getByName(server.host)
        } catch (e: IOException) {
            return PingResult.Failed
        }
        val v6 = address is Inet6Address
        val fd = try {
            if (v6) Os.socket(OsConstants.AF_INET6, OsConstants.SOCK_DGRAM, OsConstants.IPPROTO_ICMPV6)
            else Os.socket(OsConstants.AF_INET, OsConstants.SOCK_DGRAM, OsConstants.IPPROTO_ICMP)
        } catch (e: ErrnoException) {
            return PingResult.Failed
        }
        return try {
            network?.bindSocket(fd)
            echo(fd, address, v6, timeoutMs)
        } catch (e: ErrnoException) {
            PingResult.Failed
        } catch (e: IOException) {
            PingResult.Failed
        } finally {
            runCatching { Os.close(fd) }
        }
    }

    private fun echo(fd: FileDescriptor, address: InetAddress, v6: Boolean, timeoutMs: Int): PingResult {
        // Type, code, checksum, identifier, sequence, then a little payload.
        val request = ByteArray(16)
        request[0] = (if (v6) ECHO_REQUEST_V6 else ECHO_REQUEST_V4).toByte()
        request[7] = SEQUENCE.toByte()
        val reply = ByteArray(512)
        val start = System.nanoTime()
        val deadline = start + timeoutMs * 1_000_000L
        Os.sendto(fd, request, 0, request.size, 0, address, 0)
        while (true) {
            val left = ((deadline - System.nanoTime()) / 1_000_000).toInt()
            if (left <= 0) return PingResult.Failed
            val poll = StructPollfd().apply {
                this.fd = fd
                events = OsConstants.POLLIN.toShort()
            }
            if (Os.poll(arrayOf(poll), left) == 0) return PingResult.Failed
            val size = Os.recvfrom(fd, reply, 0, reply.size, 0, null)
            val type = reply[0].toInt() and 0xff
            if (size >= 8 && type == (if (v6) ECHO_REPLY_V6 else ECHO_REPLY_V4) && reply[7] == SEQUENCE.toByte()) {
                return PingResult.Ms(maxOf(1, ((System.nanoTime() - start) / 1_000_000).toInt()))
            }
        }
    }

    private companion object {
        const val ECHO_REQUEST_V4 = 8
        const val ECHO_REPLY_V4 = 0
        const val ECHO_REQUEST_V6 = 128
        const val ECHO_REPLY_V6 = 129
        const val SEQUENCE = 1
    }
}
