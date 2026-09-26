package com.alditalk.panther.util

import okhttp3.Cookie
import okhttp3.CookieJar
import okhttp3.HttpUrl

/**
 * In-memory CookieJar that stores cookies across requests.
 *
 * MotoG84-Freeze-Fix: ConcurrentHashMap + synchronized save, da OkHttp
 * Requests parallel abarbeitet; die alte mutableMapOf-Variante konnte
 * unter Rennen eine ConcurrentModificationException werfen und damit
 * den Login/Loop abschiessen.
 */
class MemoryCookieJar : CookieJar {
    private val store = java.util.concurrent.ConcurrentHashMap<String, MutableList<Cookie>>()

    override fun saveFromResponse(url: HttpUrl, cookies: List<Cookie>) {
        val host = url.host
        val existing = store.getOrPut(host) { mutableListOf() }
        synchronized(existing) {
            existing.removeAll { c -> cookies.any { it.name == c.name } }
            existing.addAll(cookies)
        }
    }

    override fun loadForRequest(url: HttpUrl): List<Cookie> {
        val existing = store[url.host] ?: return emptyList()
        return synchronized(existing) { existing.filter { it.matches(url) } }
    }
}
