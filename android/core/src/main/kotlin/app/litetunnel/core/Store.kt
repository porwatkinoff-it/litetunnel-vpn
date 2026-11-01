package app.litetunnel.core

import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.time.Duration
import java.time.Instant
import java.util.UUID

/**
 * The app's files, in one folder:
 * `state.json` (servers, subscriptions, settings), `config.json` (what the
 * tunnel runs), `rulesets/`, and the core's `work/` and `tmp/`.
 */
class Store(val root: File) {
    val stateFile = File(root, "state.json")
    val configFile = File(root, "config.json")
    val ruleSetDirectory = File(root, "rulesets")
    val workDirectory = File(root, "work")
    val tempDirectory = File(root, "tmp")

    fun prepareDirectories() {
        for (dir in listOf(root, ruleSetDirectory, workDirectory, tempDirectory)) dir.mkdirs()
    }

    /** A missing or broken file gives an empty state rather than a crash. */
    fun loadState(): AppState = try {
        StateCodec.decode(Json.parse(stateFile.readText()))
    } catch (e: Exception) {
        AppState()
    }

    fun save(state: AppState) = writeAtomically(stateFile, StateCodec.encode(state).jsonString(pretty = true).toByteArray())

    /** Writes the tunnel config for the selected server; false (and no file) when none is selected. */
    fun writeConfig(state: AppState): Boolean {
        val server = state.selectedServer
        if (server == null) {
            configFile.delete()
            return false
        }
        val config = ConfigBuilder.build(server, state.settings, ruleSetDirectory)
        writeAtomically(configFile, config.jsonString().toByteArray())
        return true
    }

    fun readConfig(): String? = try {
        configFile.readText()
    } catch (e: IOException) {
        null
    }

    companion object {
        /** Temp file and rename, so a crash mid-write never leaves half a file. */
        fun writeAtomically(file: File, data: ByteArray) {
            file.parentFile?.mkdirs()
            val temp = File(file.parentFile, ".${file.name}.tmp")
            temp.writeBytes(data)
            Files.move(temp.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
        }
    }
}

/**
 * AppState as JSON. Reading is lenient: a missing key takes its default and a
 * broken server or subscription is dropped, so an older or newer file still loads.
 */
object StateCodec {
    fun encode(state: AppState): Json = obj(
        "subscriptions" to state.subscriptions.map(::encodeSubscription),
        "servers" to state.servers.map(::encodeServer),
        "selectedServerId" to state.selectedServerId?.toString(),
        "settings" to encodeSettings(state.settings),
    )

    fun decode(json: Json): AppState {
        val subscriptions = json["subscriptions"]?.array.orEmpty().mapNotNull(::decodeSubscription)
        val servers = json["servers"]?.array.orEmpty().mapNotNull(::decodeServer)
        return AppState(
            subscriptions = subscriptions,
            servers = servers,
            selectedServerId = uuid(json["selectedServerId"])?.takeIf { id -> servers.any { it.id == id } },
            settings = json["settings"]?.let(::decodeSettings) ?: AppSettings(),
        )
    }

    private fun encodeServer(s: Server): Json = obj(
        "id" to s.id.toString(),
        "subscriptionId" to s.subscriptionId?.toString(),
        "name" to s.name,
        "proto" to s.proto.raw,
        "host" to s.host,
        "port" to s.port,
        "outbound" to s.outbound,
        "shareLink" to s.shareLink,
        "lastPing" to when (val ping = s.lastPing) {
            null -> null
            is PingResult.Ms -> ping.value
            PingResult.Failed -> "failed"
        },
    )

    private fun decodeServer(json: Json): Server? {
        val proto = ProxyProtocol.of(json["proto"]?.string) ?: return null
        val outbound = json["outbound"]?.takeIf { it is JObject } ?: return null
        return Server(
            id = uuid(json["id"]) ?: UUID.randomUUID(),
            subscriptionId = uuid(json["subscriptionId"]),
            name = json["name"]?.string ?: return null,
            proto = proto,
            host = json["host"]?.string ?: return null,
            port = json["port"]?.int ?: return null,
            outbound = outbound,
            shareLink = json["shareLink"]?.string,
            lastPing = when (val ping = json["lastPing"]) {
                is JString -> if (ping.value == "failed") PingResult.Failed else null
                null -> null
                else -> ping.int?.let(PingResult::Ms)
            },
        )
    }

    private fun encodeSubscription(s: Subscription): Json = obj(
        "id" to s.id.toString(),
        "name" to s.name,
        "url" to s.url,
        "userAgent" to s.userAgent,
        "updateIntervalHours" to s.updateIntervalHours,
        "lastUpdated" to s.lastUpdated?.toString(),
        "lastError" to s.lastError,
        "usage" to s.usage?.let {
            obj("upload" to it.upload, "download" to it.download, "total" to it.total, "expire" to it.expire?.toString())
        },
        "supportUrl" to s.supportUrl,
        "webPageUrl" to s.webPageUrl,
        "announce" to s.announce,
        "skippedCount" to s.skippedCount,
        "skippedReasons" to Json.strings(s.skippedReasons),
    )

    private fun decodeSubscription(json: Json): Subscription? {
        val usage = json["usage"]?.let {
            Usage(
                upload = it["upload"]?.long ?: 0,
                download = it["download"]?.long ?: 0,
                total = it["total"]?.long ?: 0,
                expire = instant(it["expire"]),
            )
        }
        val defaults = Subscription(name = "", url = "")
        return Subscription(
            id = uuid(json["id"]) ?: return null,
            name = json["name"]?.string ?: "",
            url = json["url"]?.string ?: return null,
            userAgent = json["userAgent"]?.string,
            updateIntervalHours = json["updateIntervalHours"]?.int ?: defaults.updateIntervalHours,
            lastUpdated = instant(json["lastUpdated"]),
            lastError = json["lastError"]?.string,
            usage = usage,
            supportUrl = json["supportUrl"]?.string,
            webPageUrl = json["webPageUrl"]?.string,
            announce = json["announce"]?.string,
            skippedCount = json["skippedCount"]?.int ?: 0,
            skippedReasons = json["skippedReasons"]?.array?.mapNotNull { it.string }.orEmpty(),
        )
    }

    private fun encodeSettings(s: AppSettings): Json = obj(
        "routing" to s.routing.raw,
        "directDomains" to s.directDomains,
        "proxyDomains" to s.proxyDomains,
        "blockDomains" to s.blockDomains,
        "remoteDns" to encodeDns(s.remoteDns),
        "directDns" to encodeDns(s.directDns),
        "killSwitch" to s.killSwitch,
        "excludeLocalNetworks" to s.excludeLocalNetworks,
        "smartSleep" to s.smartSleep,
        "tlsFragment" to s.tlsFragment,
        "blockAds" to s.blockAds,
        "sniffing" to s.sniffing,
        "ipv6" to s.ipv6,
        "mtu" to s.mtu,
        "tunStack" to s.tunStack.raw,
        "logLevel" to s.logLevel.raw,
        "sortMode" to s.sortMode.raw,
        "pingOnOpen" to s.pingOnOpen,
        "pingMode" to s.pingMode.raw,
        "pingURL" to s.pingUrl,
        "pingDisplay" to s.pingDisplay.raw,
        "pingOnUpdate" to s.pingOnUpdate,
    )

    private fun decodeSettings(json: Json): AppSettings {
        val d = AppSettings()
        fun bool(key: String, default: Boolean) = json[key]?.bool ?: default
        fun list(key: String) = json[key]?.array?.mapNotNull { it.string } ?: emptyList()
        return AppSettings(
            routing = RoutingMode.entries.firstOrNull { it.raw == json["routing"]?.string } ?: d.routing,
            directDomains = list("directDomains"),
            proxyDomains = list("proxyDomains"),
            blockDomains = list("blockDomains"),
            remoteDns = decodeDns(json["remoteDns"]) ?: d.remoteDns,
            directDns = decodeDns(json["directDns"]) ?: d.directDns,
            killSwitch = bool("killSwitch", d.killSwitch),
            excludeLocalNetworks = bool("excludeLocalNetworks", d.excludeLocalNetworks),
            smartSleep = bool("smartSleep", d.smartSleep),
            tlsFragment = bool("tlsFragment", d.tlsFragment),
            blockAds = bool("blockAds", d.blockAds),
            sniffing = bool("sniffing", d.sniffing),
            ipv6 = bool("ipv6", d.ipv6),
            mtu = json["mtu"]?.int ?: d.mtu,
            tunStack = TunStack.entries.firstOrNull { it.raw == json["tunStack"]?.string } ?: d.tunStack,
            logLevel = LogLevel.entries.firstOrNull { it.raw == json["logLevel"]?.string } ?: d.logLevel,
            sortMode = SortMode.entries.firstOrNull { it.raw == json["sortMode"]?.string } ?: d.sortMode,
            pingOnOpen = bool("pingOnOpen", d.pingOnOpen),
            pingMode = PingMode.entries.firstOrNull { it.raw == json["pingMode"]?.string } ?: d.pingMode,
            pingUrl = json["pingURL"]?.string?.takeIf { it.isNotEmpty() } ?: d.pingUrl,
            pingDisplay = PingDisplay.entries.firstOrNull { it.raw == json["pingDisplay"]?.string } ?: d.pingDisplay,
            pingOnUpdate = bool("pingOnUpdate", d.pingOnUpdate),
        )
    }

    private fun encodeDns(d: DnsServer): Json = obj("kind" to d.kind.raw, "address" to d.address)

    private fun decodeDns(json: Json?): DnsServer? {
        val kind = DnsServer.Kind.entries.firstOrNull { it.raw == json?.get("kind")?.string } ?: return null
        val address = json?.get("address")?.string?.takeIf { it.isNotEmpty() } ?: return null
        return DnsServer(kind, address)
    }

    private fun uuid(json: Json?): UUID? = try {
        json?.string?.let(UUID::fromString)
    } catch (e: IllegalArgumentException) {
        null
    }

    private fun instant(json: Json?): Instant? = try {
        json?.string?.let(Instant::parse)
    } catch (e: Exception) {
        null
    }
}

/**
 * Downloads the rule-sets the settings need, at most once a week and only
 * when the app asks (on connect), never on a timer.
 */
object RuleSetUpdater {
    val maxAge: Duration = Duration.ofDays(7)

    fun isDue(tags: List<String>, directory: File, now: Instant = Instant.now()): Boolean = tags.any { tag ->
        val file = File(directory, "$tag.srs")
        !file.exists() || Duration.between(Instant.ofEpochMilli(file.lastModified()), now) > maxAge
    }

    /** Returns the tags that could not be downloaded. Blocking. */
    fun update(tags: List<String>, directory: File, timeoutMs: Int = 20_000): List<String> {
        directory.mkdirs()
        val failed = ArrayList<String>()
        for (tag in tags) {
            val data = RuleSets.urls(tag).firstNotNullOfOrNull { download(it, timeoutMs) }
            if (data == null) {
                failed.add(tag)
            } else {
                Store.writeAtomically(File(directory, "$tag.srs"), data)
            }
        }
        return failed
    }

    private fun download(url: String, timeoutMs: Int): ByteArray? {
        val connection = try {
            URL(url).openConnection() as HttpURLConnection
        } catch (e: Exception) {
            return null
        }
        return try {
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.useCaches = false
            if (connection.responseCode != 200) return null
            connection.inputStream.use { it.readBytes() }.takeIf(RuleSets::looksValid)
        } catch (e: IOException) {
            null
        } finally {
            connection.disconnect()
        }
    }
}
