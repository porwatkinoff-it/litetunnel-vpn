package app.litetunnel.core

import java.security.MessageDigest

/**
 * What a subscription request says about this device. Panels with a device
 * limit (Remnawave and others) count devices by `x-hwid` and give clients
 * that do not send it a placeholder server instead of the list.
 * https://docs.rw/docs/features/hwid-device-limit
 */
data class DeviceInfo(
    val hwid: String,
    /** "Android". */
    val os: String,
    /** "15". */
    val osVersion: String,
    /** "Pixel 8". */
    val model: String,
) {
    val headers: Map<String, String>
        get() = mapOf(
            "x-hwid" to hwid,
            "x-device-os" to header(os),
            "x-ver-os" to header(osVersion),
            "x-device-model" to header(model),
        )

    companion object {
        /**
         * A stable ID from a per-app device ID (Android's ANDROID_ID), so a
         * reinstall does not take another of the subscription's device slots.
         * Hashed: the provider gets an ID only for this app, never the seed.
         * 32 hex characters fit the panel's rule (10-64 of A-Z, a-z, 0-9, `=`, `-`).
         */
        fun hwid(seed: String): String {
            val digest = MessageDigest.getInstance("SHA-256").digest("LiteTunnel:$seed".toByteArray())
            return digest.take(16).joinToString("") { "%02x".format(it) }
        }

        /** HTTP header values must be printable ASCII. */
        private fun header(value: String): String =
            value.map { if (it in ' '..'~') it else '_' }.joinToString("").trim().ifEmpty { "unknown" }
    }
}
