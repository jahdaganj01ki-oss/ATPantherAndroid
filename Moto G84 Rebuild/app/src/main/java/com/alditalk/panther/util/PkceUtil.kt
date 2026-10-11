package com.alditalk.panther.util

/** Generate a PKCE (code_verifier, code_challenge) pair using S256. */
data class PkcePair(val codeVerifier: String, val codeChallenge: String)

fun generatePkce(): PkcePair {
    val verifier = randomCodeVerifier()
    val challenge = verifier.sha256Bytes().base64UrlNoPad()
    return PkcePair(verifier, challenge)
}
