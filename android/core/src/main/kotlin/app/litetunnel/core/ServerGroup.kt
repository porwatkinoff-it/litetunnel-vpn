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
