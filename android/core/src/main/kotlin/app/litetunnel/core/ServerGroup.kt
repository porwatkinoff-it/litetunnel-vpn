package app.litetunnel.core

/**
 * Several servers behind one entry: what an Xray config with a balancer
 * ("Авто-выбор") is. It runs as a sing-box urltest group, which picks the
 * fastest member and moves off one that stops answering.
 *
 * Stored in `outbound` as `{"type": "urltest", "outbounds": [<member>...]}`
 * with the member outbounds inline; ConfigBuilder gives them tags.
 */
object ServerGroup {
    const val TYPE = "urltest"

    /**
     * sing-box tests every 3 minutes by default, one request per member.
     * Rarer tests cost fewer wake-ups; a dead member is still left on the
     * next test or the next failed connection.
     */
    const val INTERVAL = "10m"

    const val AUTO_NAME = "Авто-выбор"

    /** Marks the row the app adds to a subscription. The config never sees it. */
    const val AUTO_KEY = "auto"

    /** What says two outbounds are the same server: all of it but the name. */
    fun key(outbound: Json): Json = outbound.with("tag", null)

    /**
     * Every server of a list once: the single rows first, named after their
     * row, then group members those do not cover. Auto rows add nothing.
     */
    fun uniqueMembers(list: List<Server>): List<Json> {
        val seen = HashSet<Json>()
        val result = ArrayList<Json>()
        for (server in list) {
            if (!server.isGroup && seen.add(key(server.outbound))) {
                result.add(server.outbound.with("tag", JString(server.name)))
            }
        }
        for (server in list) {
            if (!server.isGroup || server.isAuto) continue
            for (member in members(server.outbound).orEmpty()) {
                if (seen.add(key(member))) result.add(member)
            }
        }
        return result
    }

    /**
     * The auto row for a subscription's servers. Null with fewer than two
     * servers, or when the provider's own group already covers them all.
     */
    fun auto(list: List<Server>): ParsedServer? {
        val all = uniqueMembers(list)
        if (all.size < 2) return null
        val covered = list.any { server ->
            val members = members(server.outbound)
            !server.isAuto && members != null && members.map(::key).toSet().size == all.size
        }
        if (covered) return null
        for (member in all) {
            val proto = ProxyProtocol.of(member["type"]?.string) ?: continue
            val host = member["server"]?.string ?: continue
            val port = member["server_port"]?.int ?: continue
            val outbound = obj("type" to TYPE, AUTO_KEY to true, "outbounds" to JArray(all))
            return ParsedServer(AUTO_NAME, proto, host, port, outbound, null)
        }
        return null
    }

    fun make(members: List<ParsedServer>, name: String): ParsedServer {
        val first = members[0]
        val outbound = obj("type" to TYPE, "outbounds" to JArray(members.map { it.outbound }))
        return ParsedServer(name, first.proto, first.host, first.port, outbound, null)
    }

    /** The member outbounds, or null when the outbound is a single server. */
    fun members(outbound: Json): List<Json>? {
        if (outbound["type"]?.string != TYPE) return null
        return outbound["outbounds"]?.array
    }

    /** The outbounds to put in the config, the first of them tagged `tag`. */
    fun outbounds(outbound: Json, tag: String): List<Json> {
        val members = members(outbound) ?: return listOf(outbound.with("tag", JString(tag)))
        val tagged = members.mapIndexed { index, member -> member.with("tag", JString("$tag-${index + 1}")) }
        val group = obj(
            "type" to TYPE,
            "tag" to tag,
            "outbounds" to Json.strings(tagged.mapNotNull { it["tag"]?.string }),
            "interval" to INTERVAL,
        )
        return listOf(group) + tagged
    }
}
