package app.litetunnel.core

import java.text.Collator
import java.time.Duration
import java.time.Instant
import java.util.Locale
import java.util.UUID

enum class ProxyProtocol(val raw: String, val title: String) {
    VLESS("vless", "VLESS"),
    VMESS("vmess", "VMess"),
    TROJAN("trojan", "Trojan"),
    SHADOWSOCKS("shadowsocks", "Shadowsocks"),
    HYSTERIA2("hysteria2", "Hysteria2"),
    TUIC("tuic", "TUIC"),
    SOCKS("socks", "SOCKS5"),
    HTTP("http", "HTTP");

    companion object {
        fun of(raw: String?): ProxyProtocol? = entries.firstOrNull { it.raw == raw }
    }
}

/** What a share link or a subscription entry turns into before it gets an ID. */
data class ParsedServer(
    val name: String,
    val proto: ProxyProtocol,
    val host: String,
    val port: Int,
    /** sing-box outbound without `tag`. */
    val outbound: Json,
    val shareLink: String?,
) {
    /** Survives a subscription refresh as long as the provider keeps the server. */
    val matchKey: String get() = "${proto.raw}|$host|$port|$name"
}

data class Server(
    val id: UUID = UUID.randomUUID(),
    val subscriptionId: UUID?,
    val name: String,
    val proto: ProxyProtocol,
    val host: String,
    val port: Int,
    val outbound: Json,
    val shareLink: String?,
    val lastPing: PingResult? = null,
) {
    constructor(subscriptionId: UUID?, parsed: ParsedServer, id: UUID = UUID.randomUUID()) : this(
        id = id,
        subscriptionId = subscriptionId,
        name = parsed.name,
        proto = parsed.proto,
        host = parsed.host,
        port = parsed.port,
        outbound = parsed.outbound,
        shareLink = parsed.shareLink,
    )

    val matchKey: String get() = "${proto.raw}|$host|$port|$name"

    val isGroup: Boolean get() = ServerGroup.members(outbound) != null

    /** The row the app adds to a subscription, as opposed to a provider's group. */
    val isAuto: Boolean get() = outbound[ServerGroup.AUTO_KEY]?.bool == true

    /** Transport and security in one short label, e.g. "Reality · Vision". */
    val detail: String
        get() {
            ServerGroup.members(outbound)?.let { return "Самый быстрый из ${it.size}" }
            val parts = ArrayList<String>()
            val tls = outbound["tls"]
            if (tls != null && tls["enabled"]?.bool == true) {
                parts.add(if (tls["reality"]?.get("enabled")?.bool == true) "Reality" else "TLS")
            }
            outbound["transport"]?.get("type")?.string?.let { parts.add(it.uppercase()) }
            if (outbound["flow"]?.string?.startsWith("xtls-rprx-vision") == true) parts.add("Vision")
            if (outbound["obfs"] != null) parts.add("obfs")
            if (outbound["server_ports"] != null) parts.add("hopping")
            return parts.joinToString(" · ")
        }

    /** The line under the name: "Протокол · транспорт", for a group its size. */
    val subtitle: String
        get() = if (isGroup) detail else listOf(proto.title, detail).filter { it.isNotEmpty() }.joinToString(" · ")

    /**
     * The servers of a group, each as its own Server; empty for a single server.
     * Built once: the proxy ping finds a member's local port by the object itself.
     */
    val members: List<Server> by lazy {
        (ServerGroup.members(outbound) ?: emptyList()).mapNotNull { member ->
            val proto = ProxyProtocol.of(member["type"]?.string) ?: return@mapNotNull null
            val host = member["server"]?.string ?: return@mapNotNull null
            val port = member["server_port"]?.int ?: return@mapNotNull null
            Server(subscriptionId, ParsedServer(member["tag"]?.string ?: "$host:$port", proto, host, port, member, null))
        }
    }
}

sealed interface PingResult {
    data class Ms(val value: Int) : PingResult
    data object Failed : PingResult

    val sortValue: Int
        get() = when (this) {
            is Ms -> value
            Failed -> Int.MAX_VALUE - 1
        }
}

data class Usage(
    val upload: Long,
    val download: Long,
    /** 0 means unlimited. */
    val total: Long,
    val expire: Instant?,
) {
    val used: Long get() = upload + download

    val fraction: Double? get() = if (total > 0) minOf(1.0, used.toDouble() / total.toDouble()) else null
}

data class Subscription(
    val id: UUID = UUID.randomUUID(),
    val name: String,
    val url: String,
    val userAgent: String? = null,
    val updateIntervalHours: Int = 24,
    val lastUpdated: Instant? = null,
    val lastError: String? = null,
    val usage: Usage? = null,
    val supportUrl: String? = null,
    val webPageUrl: String? = null,
    val announce: String? = null,
    val skippedCount: Int = 0,
    /** Why entries were skipped, without the entries: those hold keys. */
    val skippedReasons: List<String> = emptyList(),
) {
    fun isDue(now: Instant): Boolean {
        val last = lastUpdated ?: return true
        return Duration.between(last, now) >= Duration.ofHours(maxOf(1, updateIntervalHours).toLong())
    }
}

enum class RoutingMode(val raw: String) {
    /** Everything through the proxy except private networks. */
    GLOBAL("global"),

    /** Russian domains and IPs go direct, the rest through the proxy. */
    RUSSIA_DIRECT("russiaDirect"),
}

data class DnsServer(val kind: Kind, val address: String) {
    enum class Kind(val raw: String) { UDP("udp"), TLS("tls"), HTTPS("https") }

    companion object {
        val CLOUDFLARE = DnsServer(Kind.HTTPS, "1.1.1.1")
        val YANDEX = DnsServer(Kind.UDP, "77.88.8.8")
    }
}

enum class TunStack(val raw: String) { SYSTEM("system"), GVISOR("gvisor"), MIXED("mixed") }

enum class LogLevel(val raw: String) { ERROR("error"), WARN("warn"), INFO("info"), DEBUG("debug") }

enum class SortMode(val raw: String) { NONE("none"), PING("ping"), NAME("name") }

/**
 * What time a ping shows. Every mode first checks the server through
 * itself: a request to the test address and a 64 KB download. A server that
 * fails either shows "no connection", so a time means it works.
 */
enum class PingMode(val raw: String) {
    /**
     * A GET through the server, its time divided by 3.3 so it reads like a
     * plain ping rather than a full proxied round trip.
     */
    REAL("real"),
    PROXY_GET("proxyGet"),
    PROXY_HEAD("proxyHead"),

    /** A TCP handshake with the server; a QUIC one for Hysteria2 and TUIC. */
    TCP("tcp"),

    /** An ICMP echo to the server's address. */
    ICMP("icmp");

    /**
     * The time is the request's through the server. TCP and ICMP time the
     * server directly, and fall back to the request's (as REAL) when it does
     * not answer them.
     */
    val throughProxy: Boolean get() = this == REAL || this == PROXY_GET || this == PROXY_HEAD
}

enum class PingDisplay(val raw: String) {
    /** Milliseconds. */
    TIME("time"),

    /** A signal-strength scale. */
    BARS("bars"),
    BOTH("both"),

    /** A dot: answered or not. */
    DOTS("dots"),
}

data class AppSettings(
    val routing: RoutingMode = RoutingMode.RUSSIA_DIRECT,
    val directDomains: List<String> = emptyList(),
    val proxyDomains: List<String> = emptyList(),
    val blockDomains: List<String> = emptyList(),
    val remoteDns: DnsServer = DnsServer.CLOUDFLARE,
    val directDns: DnsServer = DnsServer.YANDEX,
    /** On Android this is the system "Always-on VPN" and "Block connections without VPN". */
    val killSwitch: Boolean = false,
    val excludeLocalNetworks: Boolean = true,
    val smartSleep: Boolean = true,
    val tlsFragment: Boolean = false,
    val blockAds: Boolean = false,
    val sniffing: Boolean = true,
    val ipv6: Boolean = false,
    /** 0 lets the core pick. */
    val mtu: Int = 0,
    val tunStack: TunStack = TunStack.MIXED,
    val logLevel: LogLevel = LogLevel.WARN,
    val sortMode: SortMode = SortMode.NONE,
    val pingOnOpen: Boolean = false,
    val pingMode: PingMode = PingMode.REAL,
    /** The address the proxy ping modes request. */
    val pingUrl: String = DEFAULT_PING_URL,
    val pingDisplay: PingDisplay = PingDisplay.TIME,
    val pingOnUpdate: Boolean = false,
) {
    companion object {
        const val DEFAULT_PING_URL = "http://www.gstatic.com/generate_204"
    }
}

/**
 * Everything the app keeps. Immutable: each change returns a new state, which
 * is what Compose wants to see.
 */
data class AppState(
    val subscriptions: List<Subscription> = emptyList(),
    val servers: List<Server> = emptyList(),
    val selectedServerId: UUID? = null,
    val settings: AppSettings = AppSettings(),
) {
    val selectedServer: Server? get() = servers.firstOrNull { it.id == selectedServerId }

    fun servers(subscriptionId: UUID?): List<Server> = servers.filter { it.subscriptionId == subscriptionId }

    /** Adds servers that are not already in the list. Returns the new state and the added servers. */
    fun addManual(parsed: List<ParsedServer>): Pair<AppState, List<Server>> {
        val existing = servers.filter { it.subscriptionId == null }.map { it.matchKey }.toSet()
        val added = parsed.filter { it.matchKey !in existing }.map { Server(null, it) }
        val next = copy(
            servers = servers + added,
            selectedServerId = selectedServerId ?: added.firstOrNull()?.id,
        )
        return next to added
    }

    /**
     * Replaces a subscription's servers with a fresh list, keeping IDs (and so
     * the selection and last ping) for servers that are still there.
     */
    fun apply(result: SubscriptionResult, subscriptionId: UUID, date: Instant = Instant.now()): AppState {
        val index = subscriptions.indexOfFirst { it.id == subscriptionId }
        if (index < 0) return this
        val meta = result.meta
        val old = subscriptions[index]
        val sub = old.copy(
            name = meta.title?.takeIf { it.isNotEmpty() } ?: old.name,
            updateIntervalHours = meta.updateIntervalHours ?: old.updateIntervalHours,
            usage = meta.usage ?: old.usage,
            supportUrl = meta.supportUrl ?: old.supportUrl,
            webPageUrl = meta.webPageUrl ?: old.webPageUrl,
            announce = meta.announce,
            lastUpdated = date,
            lastError = null,
            skippedCount = result.skipped.size,
            skippedReasons = result.skipped.map { it.reason }.distinct().take(5),
        )
        val subs = subscriptions.toMutableList().also { it[index] = sub }

        val previousRows = servers(subscriptionId)
        val oldByKey = LinkedHashMap<String, Server>()
        for (server in previousRows) {
            if (!server.isAuto) oldByKey.putIfAbsent(server.matchKey, server)
        }
        val rows = result.servers.map { parsed ->
            val server = Server(subscriptionId, parsed)
            val previous = oldByKey.remove(parsed.matchKey)
            if (previous != null) server.copy(id = previous.id, lastPing = previous.lastPing) else server
        }
        val fresh = withAutoRow(rows, subscriptionId, previousRows)
        val list = replacingServers(subscriptionId, fresh)

        var selected = selectedServerId
        if (selected != null && list.none { it.id == selected }) {
            // The selected server left the subscription: fall back to its first server.
            selected = fresh.firstOrNull()?.id ?: list.firstOrNull()?.id
        }
        if (selected == null) selected = fresh.firstOrNull()?.id
        return copy(subscriptions = subs, servers = list, selectedServerId = selected)
    }

    private fun withAutoRow(rows: List<Server>, subscriptionId: UUID, previous: List<Server>): List<Server> {
        val auto = ServerGroup.auto(rows) ?: return rows
        val old = previous.firstOrNull { it.isAuto }
        val row = Server(subscriptionId, auto, old?.id ?: UUID.randomUUID()).copy(lastPing = old?.lastPing)
        return listOf(row) + rows
    }

    /** Keeps the subscription's block where it was in the list. */
    private fun replacingServers(subscriptionId: UUID, fresh: List<Server>): List<Server> {
        val found = servers.indexOfFirst { it.subscriptionId == subscriptionId }
        val insertAt = if (found >= 0) found else servers.size
        val before = servers.subList(0, insertAt).filter { it.subscriptionId != subscriptionId }
        val after = servers.subList(insertAt, servers.size).filter { it.subscriptionId != subscriptionId }
        return before + fresh + after
    }

    /**
     * Gives subscriptions saved before auto rows existed their row, and drops
     * or rebuilds one that no longer matches. The same state when nothing changed.
     */
    fun syncAutoGroups(): AppState {
        var next = this
        for (sub in subscriptions) {
            val old = next.servers(sub.id)
            if (old.isEmpty()) continue
            val fresh = next.withAutoRow(old.filter { !it.isAuto }, sub.id, old)
            next = next.copy(servers = next.replacingServers(sub.id, fresh))
        }
        val selected = next.selectedServerId
        if (selected != null && next.servers.none { it.id == selected }) {
            next = next.copy(selectedServerId = next.servers.firstOrNull()?.id)
        }
        return if (next.servers == servers && next.selectedServerId == selectedServerId) this else next
    }

    /** How many different servers a subscription holds, however many rows repeat them. */
    fun serverCount(subscriptionId: UUID?): Int = ServerGroup.uniqueMembers(servers(subscriptionId)).size

    /** The subscription's rows that make up a group, or null when not all of them are listed. */
    private fun memberRows(group: Server): List<Server>? {
        if (group.subscriptionId == null) return null
        val members = ServerGroup.members(group.outbound) ?: return null
        val byKey = HashMap<Json, Server>()
        for (row in servers(group.subscriptionId)) {
            if (!row.isGroup) byKey.putIfAbsent(ServerGroup.key(row.outbound), row)
        }
        val rows = members.mapNotNull { byKey[ServerGroup.key(it)] }
        return if (rows.size == members.size) rows else null
    }

    /**
     * What to measure for these rows, each server once. A group that repeats
     * its subscription's rows is measured through them.
     */
    fun pingTargets(list: List<Server>): List<Server> {
        val seen = HashSet<UUID>()
        val result = ArrayList<Server>()
        for (server in list) {
            for (target in memberRows(server) ?: listOf(server)) {
                if (seen.add(target.id)) result.add(target)
            }
        }
        return result
    }

    /** These rows and the groups built from them: the rows that show their pings. */
    fun rowsShowing(ids: Set<UUID>): Set<UUID> {
        if (ids.isEmpty()) return emptySet()
        val result = HashSet(ids)
        for (group in servers) {
            if (!group.isGroup || group.id in ids) continue
            if (memberRows(group)?.any { it.id in ids } == true) result.add(group.id)
        }
        return result
    }

    /** Records a result, and the fastest on the groups the row belongs to. */
    fun settingPing(result: PingResult?, id: UUID): AppState {
        val next = updateServer(id) { it.copy(lastPing = result) }
        val row = next.servers.firstOrNull { it.id == id } ?: return this
        if (row.isGroup || row.subscriptionId == null) return next
        return next.servers(row.subscriptionId).filter { it.isGroup }.fold(next) { state, group ->
            val rows = state.memberRows(group)
            if (rows == null || rows.none { it.id == id }) state
            else state.updateServer(group.id) { it.copy(lastPing = Ping.fastest(rows.map { r -> r.lastPing })) }
        }
    }

    fun removeSubscription(id: UUID): AppState {
        val list = servers.filter { it.subscriptionId != id }
        val selected = selectedServerId?.takeIf { s -> list.any { it.id == s } } ?: list.firstOrNull()?.id
        return copy(subscriptions = subscriptions.filter { it.id != id }, servers = list, selectedServerId = selected)
    }

    fun removeServer(id: UUID): AppState {
        val list = servers.filter { it.id != id }
        val selected = if (selectedServerId == id) list.firstOrNull()?.id else selectedServerId
        return copy(servers = list, selectedServerId = selected)
    }

    fun updateServer(id: UUID, change: (Server) -> Server): AppState =
        copy(servers = servers.map { if (it.id == id) change(it) else it })

    fun updateSubscription(id: UUID, change: (Subscription) -> Subscription): AppState =
        copy(subscriptions = subscriptions.map { if (it.id == id) change(it) else it })

    fun sorted(list: List<Server>): List<Server> {
        if (settings.sortMode == SortMode.NONE) return list
        // Groups stay on top in any order: they are not one more server to compare.
        val (groups, rest) = list.partition { it.isGroup }
        return groups + when (settings.sortMode) {
            SortMode.NAME -> rest.sortedWith(compareBy(NaturalOrder) { it.name })
            // sortedBy is stable: servers without a result keep their order at the end.
            else -> rest.sortedBy { it.lastPing?.sortValue ?: Int.MAX_VALUE }
        }
    }
}

/**
 * Finder-style order, like localizedStandardCompare: case-insensitive, and
 * numbers compare by value, so "Server 2" comes before "Server 10".
 */
object NaturalOrder : Comparator<String> {
    private val collator = Collator.getInstance(Locale.getDefault()).apply { strength = Collator.SECONDARY }
    private val chunk = Regex("\\d+|\\D+")

    override fun compare(a: String, b: String): Int {
        val x = chunk.findAll(a).map { it.value }.toList()
        val y = chunk.findAll(b).map { it.value }.toList()
        for (i in 0 until minOf(x.size, y.size)) {
            val p = x[i]
            val q = y[i]
            val c = if (p[0].isDigit() && q[0].isDigit()) {
                p.trimStart('0').length.compareTo(q.trimStart('0').length)
                    .takeIf { it != 0 } ?: p.trimStart('0').compareTo(q.trimStart('0'))
            } else {
                collator.compare(p, q)
            }
            if (c != 0) return c
        }
        return x.size.compareTo(y.size)
    }
}
