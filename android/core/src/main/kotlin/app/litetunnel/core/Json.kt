package app.litetunnel.core

/**
 * A JSON tree. Servers keep their sing-box outbound in this form, so a link,
 * a sing-box subscription and a hand-edited server all end up the same way.
 */
sealed interface Json {
    operator fun get(key: String): Json? = (this as? JObject)?.map?.get(key)
    operator fun get(index: Int): Json? = (this as? JArray)?.items?.getOrNull(index)

    val string: String? get() = (this as? JString)?.value

    val long: Long?
        get() = when (this) {
            is JInt -> value
            is JDouble -> if (value % 1.0 == 0.0 && value >= Long.MIN_VALUE && value <= Long.MAX_VALUE) value.toLong() else null
            is JString -> value.toLongOrNull()
            else -> null
        }

    val int: Int? get() = long?.takeIf { it in Int.MIN_VALUE..Int.MAX_VALUE }?.toInt()
    val bool: Boolean? get() = (this as? JBool)?.value
    val array: List<Json>? get() = (this as? JArray)?.items
    val obj: Map<String, Json>? get() = (this as? JObject)?.map

    /** A copy of this object with `key` set, or removed when `value` is null. */
    fun with(key: String, value: Json?): Json {
        val map = (this as? JObject)?.map ?: return this
        val next = LinkedHashMap(map)
        if (value == null) next.remove(key) else next[key] = value
        return JObject(next)
    }

    fun jsonString(pretty: Boolean = false): String = buildString { JsonWriter.write(this@Json, this, pretty, 0) }

    companion object {
        fun parse(text: String): Json = JsonParser(text).parseDocument()

        fun strings(values: List<String>): Json = JArray(values.map(::JString))
    }
}

data object JNull : Json
data class JBool(val value: Boolean) : Json
data class JInt(val value: Long) : Json
data class JDouble(val value: Double) : Json
data class JString(val value: String) : Json
data class JArray(val items: List<Json>) : Json
data class JObject(val map: Map<String, Json>) : Json

class JsonException(message: String) : Exception(message)

/** Builds an object; null values are left out, Kotlin values become JSON. */
fun obj(vararg pairs: Pair<String, Any?>): Json {
    val map = LinkedHashMap<String, Json>()
    for ((key, value) in pairs) {
        if (value != null) map[key] = toJson(value)
    }
    return JObject(map)
}

fun arr(vararg items: Any): Json = JArray(items.map(::toJson))

fun toJson(value: Any?): Json = when (value) {
    null -> JNull
    is Json -> value
    is String -> JString(value)
    is Boolean -> JBool(value)
    is Int -> JInt(value.toLong())
    is Long -> JInt(value)
    is Double -> JDouble(value)
    is List<*> -> JArray(value.map(::toJson))
    is Map<*, *> -> JObject(value.entries.associate { it.key.toString() to toJson(it.value) })
    else -> throw IllegalArgumentException("not JSON: ${value::class}")
}

private object JsonWriter {
    fun write(value: Json, out: StringBuilder, pretty: Boolean, depth: Int) {
        when (value) {
            JNull -> out.append("null")
            is JBool -> out.append(value.value)
            is JInt -> out.append(value.value)
            is JDouble -> writeDouble(value.value, out)
            is JString -> writeString(value.value, out)
            is JArray -> {
                if (value.items.isEmpty()) {
                    out.append("[]"); return
                }
                out.append('[')
                value.items.forEachIndexed { i, item ->
                    if (i > 0) out.append(',')
                    newline(out, pretty, depth + 1)
                    write(item, out, pretty, depth + 1)
                }
                newline(out, pretty, depth)
                out.append(']')
            }
            is JObject -> {
                if (value.map.isEmpty()) {
                    out.append("{}"); return
                }
                out.append('{')
                // Sorted, like the Swift version: the same server gives the same text.
                value.map.keys.sorted().forEachIndexed { i, key ->
                    if (i > 0) out.append(',')
                    newline(out, pretty, depth + 1)
                    writeString(key, out)
                    out.append(if (pretty) " : " else ":")
                    write(value.map.getValue(key), out, pretty, depth + 1)
                }
                newline(out, pretty, depth)
                out.append('}')
            }
        }
    }

    private fun newline(out: StringBuilder, pretty: Boolean, depth: Int) {
        if (!pretty) return
        out.append('\n')
        repeat(depth) { out.append("  ") }
    }

    private fun writeDouble(d: Double, out: StringBuilder) {
        when {
            d.isNaN() || d.isInfinite() -> out.append("null")
            d % 1.0 == 0.0 && kotlin.math.abs(d) < 1e15 -> out.append(d.toLong())
            else -> out.append(d)
        }
    }

    private fun writeString(s: String, out: StringBuilder) {
        out.append('"')
        for (c in s) {
            when (c) {
                '"' -> out.append("\\\"")
                '\\' -> out.append("\\\\")
                '\n' -> out.append("\\n")
                '\r' -> out.append("\\r")
                '\t' -> out.append("\\t")
                '\b' -> out.append("\\b")
                '\u000C' -> out.append("\\f")
                else -> if (c < ' ') out.append(String.format("\\u%04x", c.code)) else out.append(c)
            }
        }
        out.append('"')
    }
}

private class JsonParser(private val text: String) {
    private var i = if (text.startsWith('﻿')) 1 else 0

    fun parseDocument(): Json {
        val value = parseValue()
        skipSpace()
        if (i != text.length) fail("лишние символы")
        return value
    }

    private fun parseValue(): Json {
        skipSpace()
        if (i >= text.length) fail("конец текста")
        return when (val c = text[i]) {
            '{' -> parseObject()
            '[' -> parseArray()
            '"' -> JString(parseString())
            't' -> literal("true", JBool(true))
            'f' -> literal("false", JBool(false))
            'n' -> literal("null", JNull)
            else -> if (c == '-' || c in '0'..'9') parseNumber() else fail("символ $c")
        }
    }

    private fun parseObject(): Json {
        i++
        val map = LinkedHashMap<String, Json>()
        skipSpace()
        if (peek() == '}') {
            i++; return JObject(map)
        }
        while (true) {
            skipSpace()
            if (peek() != '"') fail("ключ")
            val key = parseString()
            skipSpace()
            if (peek() != ':') fail(":")
            i++
            map[key] = parseValue()
            skipSpace()
            when (peek()) {
                ',' -> i++
                '}' -> {
                    i++; return JObject(map)
                }
                else -> fail(", или }")
            }
        }
    }

    private fun parseArray(): Json {
        i++
        val items = ArrayList<Json>()
        skipSpace()
        if (peek() == ']') {
            i++; return JArray(items)
        }
        while (true) {
            items.add(parseValue())
            skipSpace()
            when (peek()) {
                ',' -> i++
                ']' -> {
                    i++; return JArray(items)
                }
                else -> fail(", или ]")
            }
        }
    }

    private fun parseString(): String {
        i++
        val out = StringBuilder()
        while (true) {
            if (i >= text.length) fail("строка не закрыта")
            val c = text[i++]
            when {
                c == '"' -> return out.toString()
                c == '\\' -> {
                    if (i >= text.length) fail("экранирование")
                    when (val e = text[i++]) {
                        '"' -> out.append('"')
                        '\\' -> out.append('\\')
                        '/' -> out.append('/')
                        'b' -> out.append('\b')
                        'f' -> out.append('\u000C')
                        'n' -> out.append('\n')
                        'r' -> out.append('\r')
                        't' -> out.append('\t')
                        'u' -> {
                            if (i + 4 > text.length) fail("\\u")
                            val code = text.substring(i, i + 4).toIntOrNull(16) ?: fail("\\u")
                            out.append(code.toChar())
                            i += 4
                        }
                        else -> fail("экранирование \\$e")
                    }
                }
                c < ' ' -> fail("управляющий символ в строке")
                else -> out.append(c)
            }
        }
    }

    private fun parseNumber(): Json {
        val start = i
        if (peek() == '-') i++
        while (i < text.length && (text[i].isDigit() || text[i] in ".eE+-")) i++
        val raw = text.substring(start, i)
        if (raw.none { it in ".eE" }) raw.toLongOrNull()?.let { return JInt(it) }
        val d = raw.toDoubleOrNull() ?: fail("число $raw")
        // An integral number reads as an integer, as JSONDecoder does on Apple platforms.
        if (d % 1.0 == 0.0 && kotlin.math.abs(d) < 9.0e15) return JInt(d.toLong())
        return JDouble(d)
    }

    private fun literal(word: String, value: Json): Json {
        if (!text.startsWith(word, i)) fail(word)
        i += word.length
        return value
    }

    private fun peek(): Char? = text.getOrNull(i)

    private fun skipSpace() {
        while (i < text.length && text[i] in " \t\n\r") i++
    }

    private fun fail(what: String): Nothing = throw JsonException("JSON: $what (позиция $i)")
}
