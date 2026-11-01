package app.litetunnel.core

import java.net.URI

/** What pasted text, a QR code or a `litetunnel://` link turns out to be. */
sealed interface ImportItem {
    data class Subscription(val url: String, val name: String?) : ImportItem
    data class Servers(val servers: List<ParsedServer>, val skipped: List<SkippedEntry>) : ImportItem
    data class Nothing(val reason: String) : ImportItem
}

object Importer {
    const val SCHEME = "litetunnel"

    fun classify(text: String): ImportItem {
        val raw = text.trim()
        if (raw.isEmpty()) return ImportItem.Nothing("Пусто")
        if (raw.lowercase().startsWith("$SCHEME:")) return deepLink(raw)
        if (raw.none { it == '\n' || it == '\r' } && isWebUrl(raw)) return ImportItem.Subscription(raw, null)
        val parsed = SubscriptionParser.parseBody(raw.toByteArray())
        if (parsed.servers.isNotEmpty()) return ImportItem.Servers(parsed.servers, parsed.skipped)
        parsed.skipped.firstOrNull()?.let { return ImportItem.Nothing(it.reason) }
        return ImportItem.Nothing("Не похоже на ссылку сервера или подписки")
    }

    /**
     * `litetunnel://add/<url or share link>`, as it is or percent-encoded, and
     * `litetunnel://import?url=<encoded>&name=<name>`.
     */
    fun deepLink(raw: String): ImportItem {
        val lower = raw.lowercase()
        val addPrefix = "$SCHEME://add/"
        if (lower.startsWith(addPrefix)) {
            val payload = raw.substring(addPrefix.length)
            return classify(if ("://" in payload) payload else percentDecode(payload) ?: payload)
        }
        val importPrefix = "$SCHEME://import"
        if (lower.startsWith(importPrefix) && raw.getOrNull(importPrefix.length).let { it == null || it in "/?#" }) {
            val query = LinkUri.parseQuery(raw.substringAfter('?', "").substringBefore('#'))
            val target = query["url"]
            if (target != null) {
                val item = classify(target)
                if (item is ImportItem.Subscription) return ImportItem.Subscription(item.url, query["name"])
                return item
            }
        }
        return ImportItem.Nothing("Неизвестная ссылка $SCHEME://")
    }

    private fun isWebUrl(s: String): Boolean = try {
        val uri = URI(s)
        uri.scheme?.lowercase() in setOf("http", "https") && !uri.host.isNullOrEmpty()
    } catch (e: Exception) {
        false
    }
}
