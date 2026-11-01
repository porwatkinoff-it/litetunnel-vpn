package app.litetunnel.core

import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException

object Base64Text {
    /** Decodes standard or URL-safe base64, with or without padding and line breaks. */
    fun decode(string: String): ByteArray? {
        var s = string.filterNot { it.isWhitespace() }
        if (s.isEmpty()) return null
        s = s.replace('-', '+').replace('_', '/').trimEnd('=')
        when (s.length % 4) {
            1 -> return null
            2 -> s += "=="
            3 -> s += "="
        }
        return try {
            java.util.Base64.getDecoder().decode(s)
        } catch (e: IllegalArgumentException) {
            null
        }
    }

    fun decodeString(string: String): String? = decode(string)?.let(::utf8OrNull)

    /** The bytes as UTF-8 text, or null when they are not valid UTF-8. */
    fun utf8OrNull(bytes: ByteArray): String? = try {
        Charsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(bytes)).toString()
    } catch (e: CharacterCodingException) {
        null
    }
}
