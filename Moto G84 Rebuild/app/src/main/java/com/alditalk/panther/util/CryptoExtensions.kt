package com.alditalk.panther.util

import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64

/** SHA-1 hex digest. */
fun String.sha1(): String {
    val md = MessageDigest.getInstance("SHA-1")
    return md.digest(toByteArray()).toHexString()
}

/** SHA-256 raw bytes. */
fun String.sha256Bytes(): ByteArray {
    val md = MessageDigest.getInstance("SHA-256")
    return md.digest(toByteArray())
}

/** ByteArray → lowercase hex string. */
fun ByteArray.toHexString(): String = joinToString("") { "%02x".format(it) }

/** Base64url-encode without padding (RFC 7636). */
fun ByteArray.base64UrlNoPad(): String =
    Base64.getUrlEncoder().withoutPadding().encodeToString(this)

/** Generate 32 random bytes, base64url-encoded, no padding → 43 chars. */
fun randomCodeVerifier(): String =
    ByteArray(32).apply { SecureRandom().nextBytes(this) }.base64UrlNoPad()
