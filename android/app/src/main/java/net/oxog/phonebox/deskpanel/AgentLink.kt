package net.oxog.phonebox.deskpanel

import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.SystemClock
import android.provider.Settings
import androidx.webkit.WebViewCompat
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.Socket
import java.net.URL
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Locale
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/**
 * Connection to the PhoneBox agent on the PC.
 *
 * 1. USB: http://127.0.0.1:8765, tunnelled by "adb reverse" while the cable is plugged in.
 * 2. Wi-Fi / Tailscale: https://<pc-address>:<port>. The address list, a token and the SHA-256 fingerprint of the
 *    agent's self-signed certificate are fetched over USB ("pairing") and stored. Only that exact certificate is
 *    trusted (pinning), and every request carries the token.
 *
 * The phone remembers every computer it was paired with; one is active over Wi-Fi (the one last plugged in, or the one
 * picked in the panel's page overview). A USB cable always wins: the plugged-in computer is used and becomes active.
 */
class AgentLink(context: Context) {

    class Reply(val code: Int, val contentType: String?, val body: InputStream)

    enum class Route { USB, WIFI, NONE }

    @Volatile var route: Route = Route.NONE
        private set

    private val prefs = context.getSharedPreferences("pairing", Context.MODE_PRIVATE)

    /** A computer this phone is paired with; the certificate fingerprint is its id. */
    private class Pc(val id: String, val name: String, val hosts: List<String>, val port: Int, val token: String) {
        fun json(): JSONObject = JSONObject().put("id", id).put("name", name).put("hosts", JSONArray(hosts)).put("port", port).put("token", token)
        companion object {
            fun from(j: JSONObject) = Pc(j.getString("id"), j.optString("name"),
                j.getJSONArray("hosts").let { a -> (0 until a.length()).map { a.getString(it) } }, j.getInt("port"), j.getString("token"))
        }
    }

    @Volatile private var pcs: List<Pc> = loadPcs()
    @Volatile private var activeId: String? = prefs.getString("active", null)

    private fun loadPcs(): List<Pc> {
        // 0.6.x stored a single pairing in plain keys; move it into the list
        if (!prefs.contains("pcs") && prefs.getBoolean("enabled", false)) {
            val fp = prefs.getString("fingerprint", null)
            val token = prefs.getString("token", null)
            if (fp != null && token != null) {
                val pc = Pc(fp, prefs.getString("pc", "") ?: "", prefs.getString("hosts", "")!!.split(',').filter { it.isNotBlank() }, prefs.getInt("port", 8766), token)
                prefs.edit().clear().putString("pcs", JSONArray().put(pc.json()).toString()).putString("active", fp).apply()
            }
        }
        return try {
            val a = JSONArray(prefs.getString("pcs", "[]"))
            (0 until a.length()).mapNotNull { runCatching { Pc.from(a.getJSONObject(it)) }.getOrNull() }
        } catch (_: Exception) { emptyList() }
    }

    /**
     * Guards every read-modify-save of the computer list (USB re-pairing, a QR pairing, select, forget): each one
     * reads [pcs], filters it and saves it, and two at once would drop the other's change.
     */
    private val pcsLock = Any()

    private fun savePcs(list: List<Pc>, active: String?) {
        pcs = list
        activeId = active
        prefs.edit().putString("pcs", JSONArray().apply { list.forEach { put(it.json()) } }.toString()).putString("active", active).apply()
    }

    private fun activePc(): Pc? = pcs.firstOrNull { it.id == activeId } ?: pcs.firstOrNull()

    /** The paired computers for the panel: [{"id", "name", "active"}]. */
    fun pcsJson(): String {
        val active = activePc()?.id
        return JSONArray().apply { pcs.forEach { put(JSONObject().put("id", it.id).put("name", it.name).put("active", it.id == active)) } }.toString()
    }

    /** Makes another paired computer the one used over Wi-Fi. */
    fun select(id: String): Boolean = synchronized(pcsLock) {
        if (pcs.none { it.id == id }) return false
        savePcs(pcs, id)
        factory = null
        preferredHost = 0
        return true
    }

    fun forget(id: String) = synchronized(pcsLock) {
        val rest = pcs.filter { it.id != id }
        savePcs(rest, if (activeId == id) rest.firstOrNull()?.id else activeId)
    }
    private val appVersion = context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: ""
    // Identifies this phone to the agent (several phones can show different pages): a short hash of the app-scoped
    // Android id, and the model name.
    private val deviceId = MessageDigest.getInstance("SHA-256")
        .digest((Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: "unknown").toByteArray())
        .take(6).joinToString("") { "%02x".format(it) }
    private val deviceModel = "${Build.MANUFACTURER} ${Build.MODEL}".filter { it in ' '..'~' }.trim().take(40)
    private val webViewInfo = runCatching {
        WebViewCompat.getCurrentWebViewPackage(context)?.let { p ->
            "${p.packageName}/${p.versionName ?: ""}".filter { it in ' '..'~' }.trim().take(80)
        } ?: ""
    }.getOrDefault("")
    private val background = Executors.newSingleThreadExecutor()
    private val pinnedHostnameVerifier = HostnameVerifier { _, _ -> true }
    private val tlsSocketCount = AtomicLong(0)
    @Volatile private var lastPair = 0L
    @Volatile private var lastUsbFailAt = 0L
    @Volatile private var preferredHost = 0
    @Volatile private var factory: Pair<String, SSLSocketFactory>? = null
    @Volatile private var lastRemoteStatus = 200

    private class RemoteSample(val atMs: Long, val totalMs: Double, val intervalMs: Double, val reused: Boolean, val route: String)
    private val remoteStatsLock = Any()
    private val remoteSamples = ArrayDeque<RemoteSample>(260)
    private var lastRemoteSampleAt = 0L

    private val pendingMoveDx = AtomicInteger(0)
    private val pendingMoveDy = AtomicInteger(0)
    private val pendingScrollDx = AtomicInteger(0)
    private val pendingScrollDy = AtomicInteger(0)
    private val remoteWakeLock = Object()
    @Volatile private var remoteWorkerStarted = false

    /** Accumulates move/scroll deltas and flushes them on a background thread without blocking on per-response JS waits. */
    fun enqueueRemoteDelta(op: String, dx: Int, dy: Int) {
        if (dx == 0 && dy == 0) return
        when (op) {
            "move" -> { pendingMoveDx.addAndGet(dx); pendingMoveDy.addAndGet(dy) }
            "scroll" -> { pendingScrollDx.addAndGet(dx); pendingScrollDy.addAndGet(dy) }
            else -> return
        }
        synchronized(remoteWakeLock) {
            if (!remoteWorkerStarted) {
                remoteWorkerStarted = true
                Thread(::runRemoteSender, "phonebox-remote").apply { isDaemon = true; start() }
            }
            remoteWakeLock.notifyAll()
        }
    }

    private fun runRemoteSender() {
        while (true) {
            val mdx = pendingMoveDx.getAndSet(0)
            val mdy = pendingMoveDy.getAndSet(0)
            val sdx = pendingScrollDx.getAndSet(0)
            val sdy = pendingScrollDy.getAndSet(0)
            if (mdx == 0 && mdy == 0 && sdx == 0 && sdy == 0) {
                synchronized(remoteWakeLock) {
                    if (pendingMoveDx.get() == 0 && pendingMoveDy.get() == 0 && pendingScrollDx.get() == 0 && pendingScrollDy.get() == 0) {
                        try { remoteWakeLock.wait(1000) } catch (_: InterruptedException) {}
                    }
                }
                continue
            }
            val start = SystemClock.elapsedRealtime()
            if (mdx != 0 || mdy != 0) {
                val sx = mdx.coerceIn(-2000, 2000)
                val sy = mdy.coerceIn(-2000, 2000)
                if (sx != mdx) pendingMoveDx.addAndGet(mdx - sx)
                if (sy != mdy) pendingMoveDy.addAndGet(mdy - sy)
                request("POST", "/api/remote?op=move&dx=$sx&dy=$sy")
            }
            if (sdx != 0 || sdy != 0) {
                val sx = sdx.coerceIn(-1200, 1200)
                val sy = sdy.coerceIn(-1200, 1200)
                if (sx != sdx) pendingScrollDx.addAndGet(sdx - sx)
                if (sy != sdy) pendingScrollDy.addAndGet(sdy - sy)
                request("POST", "/api/remote?op=scroll&dx=$sx&dy=$sy")
            }
            val rem = 12L - (SystemClock.elapsedRealtime() - start)
            if (rem > 0) try { Thread.sleep(rem) } catch (_: InterruptedException) {}
        }
    }

    private fun recordRemoteSample(totalMs: Double, reused: Boolean, usedRoute: Route) {
        val now = SystemClock.elapsedRealtime()
        synchronized(remoteStatsLock) {
            val interval = if (lastRemoteSampleAt > 0L) (now - lastRemoteSampleAt).toDouble() else 0.0
            lastRemoteSampleAt = now
            remoteSamples.addLast(RemoteSample(now, totalMs, interval, reused, usedRoute.name.lowercase(Locale.US)))
            if (remoteSamples.size > 256) remoteSamples.removeFirst()
            while (remoteSamples.isNotEmpty() && now - remoteSamples.first().atMs > 5000L) remoteSamples.removeFirst()
        }
    }

    fun remoteStatsJson(): String {
        val now = SystemClock.elapsedRealtime()
        val snap = synchronized(remoteStatsLock) {
            while (remoteSamples.isNotEmpty() && now - remoteSamples.first().atMs > 5000L) remoteSamples.removeFirst()
            remoteSamples.toList()
        }
        val j = JSONObject().put("route", route.name.lowercase(Locale.US)).put("status", lastRemoteStatus)
        if (snap.isEmpty()) return j.put("count", 0).toString()
        val times = snap.map { it.totalMs }.sorted()
        val p95 = times[minOf(times.size - 1, (times.size * 0.95).toInt())]
        val intervals = snap.drop(1).map { it.intervalMs }.filter { it in 1.0..1000.0 }
        val reusedCount = snap.count { it.reused }
        return j.put("count", snap.size)
            .put("reused", reusedCount)
            .put("newTls", snap.size - reusedCount)
            .put("minMs", times.first())
            .put("avgMs", times.average())
            .put("p95Ms", p95)
            .put("maxMs", times.last())
            .put("avgIntervalMs", if (intervals.isEmpty()) 0.0 else intervals.average())
            .toString()
    }

    /** Opens a long-lived GET (the live stream) over the best route; null when the PC is unreachable. */
    fun stream(path: String): Pair<Route, Reply>? {
        usb("GET", path, STREAM_READ_TIMEOUT)?.let { route = Route.USB; return Route.USB to it }
        wifi("GET", path, STREAM_READ_TIMEOUT)?.let { route = Route.WIFI; return Route.WIFI to it }
        return null
    }

    /** Forwards a request (method + path with query) to the agent over the best available route. */
    fun request(method: String, pathAndQuery: String): Reply {
        val isRemote = pathAndQuery.startsWith("/api/remote")
        val timeout = if (isRemote) REMOTE_READ_TIMEOUT else 4000
        val t0 = if (isRemote) SystemClock.elapsedRealtimeNanos() else 0L
        val tlsBefore = if (isRemote) tlsSocketCount.get() else 0L
        val tryUsbFirst = route != Route.WIFI || (!isRemote && System.currentTimeMillis() - lastUsbFailAt > 3000L)
        if (tryUsbFirst) {
            usb(method, pathAndQuery, timeout)?.let {
                route = Route.USB
                if (!isRemote) maybePair()
                else {
                    lastRemoteStatus = it.code
                    recordRemoteSample((SystemClock.elapsedRealtimeNanos() - t0) / 1e6, true, Route.USB)
                }
                return it
            }
            lastUsbFailAt = System.currentTimeMillis()
        }
        wifi(method, pathAndQuery, timeout)?.let {
            route = Route.WIFI
            if (isRemote) {
                lastRemoteStatus = it.code
                val reused = tlsSocketCount.get() == tlsBefore
                recordRemoteSample((SystemClock.elapsedRealtimeNanos() - t0) / 1e6, reused, Route.WIFI)
            }
            return it
        }
        if (!tryUsbFirst) {
            usb(method, pathAndQuery, timeout)?.let {
                route = Route.USB
                if (isRemote) {
                    lastRemoteStatus = it.code
                    recordRemoteSample((SystemClock.elapsedRealtimeNanos() - t0) / 1e6, true, Route.USB)
                }
                return it
            }
        }
        route = Route.NONE
        if (isRemote) {
            lastRemoteStatus = 503
            recordRemoteSample((SystemClock.elapsedRealtimeNanos() - t0) / 1e6, false, Route.NONE)
        }
        return Reply(503, "application/json", ByteArrayInputStream("""{"error":"offline"}""".toByteArray()))
    }

    private fun usb(method: String, pathAndQuery: String, readTimeout: Int = 4000): Reply? = try {
        val c = URL("$USB_BASE$pathAndQuery").openConnection() as HttpURLConnection
        c.connectTimeout = 400 // refused instantly when no adb reverse tunnel exists
        send(c, method, readTimeout)
    } catch (e: Exception) {
        null
    }

    private fun wifi(method: String, pathAndQuery: String, readTimeout: Int = 4000): Reply? {
        val pc = activePc() ?: return null
        val hosts = pc.hosts
        val token = pc.token
        val pin = pc.id
        val port = pc.port
        if (hosts.isEmpty()) return null
        // Reuse one socket factory per pinned certificate so HTTPS connections are kept alive between requests.
        val f = factory?.takeIf { it.first == pin }?.second ?: pinnedFactory(pin).also { factory = pin to it }
        // Start with the address that worked last time.
        val order = hosts.indices.sortedBy { if (it == preferredHost) -1 else it }
        for (i in order) {
            try {
                val c = URL("https://${hosts[i]}:$port$pathAndQuery").openConnection() as HttpsURLConnection
                c.sslSocketFactory = f
                c.hostnameVerifier = pinnedHostnameVerifier // singleton reference enables connection pooling
                c.connectTimeout = minOf(800, readTimeout)
                c.setRequestProperty("Authorization", "Bearer $token")
                val r = send(c, method, readTimeout)
                preferredHost = i
                return r
            } catch (e: Exception) {
                // try the next address
            }
        }
        return null
    }

    private fun send(c: HttpURLConnection, method: String, readTimeout: Int = 4000): Reply {
        // every POST acts on the computer (a container restart, a Home Assistant switch, a macro): none may run twice
        val controlPost = method == "POST"
        c.requestMethod = method
        c.setRequestProperty("X-PhoneBox-App", appVersion) // lets the agent offer updates
        c.setRequestProperty("X-PhoneBox-Device", deviceId)
        c.setRequestProperty("X-PhoneBox-Model", deviceModel)
        if (webViewInfo.isNotEmpty()) c.setRequestProperty("X-PhoneBox-WebView", webViewInfo)
        // A workflow or service restart can take longer than a state poll, whereas /api/remote stays snappy.
        c.readTimeout = if (controlPost && readTimeout != REMOTE_READ_TIMEOUT) 30000 else readTimeout
        c.useCaches = false
        if (method == "POST") {
            c.doOutput = true
            c.setFixedLengthStreamingMode(0)
            c.outputStream.close()
        }
        val code = try { c.responseCode } catch (e: java.io.IOException) {
            if (!controlPost) throw e
            // The command body was already submitted. Trying the Wi-Fi route now could run a macro or toggle twice.
            c.disconnect()
            return Reply(504, "application/json", ByteArrayInputStream("""{"ok":false,"error":"The command result is unknown; check the computer before retrying"}""".toByteArray()))
        }
        val raw = if (code >= 400) c.errorStream else c.inputStream
        // Eagerly drain & close /api/remote responses so HttpURLConnection returns the keep-alive socket to the pool immediately.
        val body = if (readTimeout == REMOTE_READ_TIMEOUT) {
            ByteArrayInputStream(raw?.use { it.readBytes() } ?: ByteArray(0))
        } else {
            raw ?: ByteArrayInputStream(ByteArray(0))
        }
        return Reply(code, c.contentType, body)
    }

    /** While on USB, refresh the Wi-Fi pairing data about once a minute. */
    private fun maybePair() {
        val now = System.currentTimeMillis()
        if (now - lastPair < 60_000) return
        lastPair = now
        background.execute {
            try {
                val c = URL("$USB_BASE/api/pair").openConnection() as HttpURLConnection
                c.connectTimeout = 800
                c.readTimeout = 3000
                val j = JSONObject(c.inputStream.bufferedReader().readText())
                val name = j.optString("pc")
                if (j.optBoolean("enabled")) {
                    // add or refresh this computer and make it the active one
                    val hosts = j.getJSONArray("hosts")
                    val pc = Pc(j.getString("fingerprint"), name, (0 until hosts.length()).map { hosts.getString(it) }, j.getInt("port"), j.getString("token"))
                    synchronized(pcsLock) { savePcs(pcs.filter { it.id != pc.id && !(name.isNotEmpty() && it.name == name) } + pc, pc.id) }
                } else if (name.isNotEmpty()) {
                    // Wi-Fi was turned off on this computer: forget its pairing, keep the others
                    synchronized(pcsLock) {
                        val rest = pcs.filter { it.name != name }
                        if (rest.size != pcs.size) savePcs(rest, rest.firstOrNull { it.id == activeId }?.id ?: rest.firstOrNull()?.id)
                    }
                }
            } catch (_: Exception) {
            }
        }
    }

    fun pairedPc(): String? = activePc()?.name

    /**
     * Pairing without USB: the computer's settings page shows a QR code with this link, and the phone trades the
     * one-time code for the Wi-Fi key. The certificate fingerprint in the link is pinned from the first connection,
     * as with USB pairing, so the key only ever goes to that computer.
     */
    class PairLink(val pc: String, val fingerprint: String, val port: Int, val hosts: List<String>, val code: String) {
        /**
         * The start of the certificate fingerprint, shown on the computer next to the QR code and in the phone's
         * confirmation: a link from anyone else (another computer, a message) shows a different code.
         */
        val check: String get() = fingerprint.take(8).uppercase().chunked(4).joinToString(" ")

        companion object {
            private val FINGERPRINT = Regex("^[0-9a-f]{64}$")
            private val CODE = Regex("^[A-Za-z0-9_-]{16,64}$")

            /**
             * Only the addresses the agent offers: private network (10/8, 172.16/12, 192.168/16) and Tailscale
             * (100.64/10) IPv4. A link pointing anywhere else is not a PhoneBox pairing link.
             */
            fun isLocalAddress(host: String): Boolean {
                val p = host.split('.')
                if (p.size != 4) return false
                val b = p.map { s -> s.toIntOrNull()?.takeIf { s.length in 1..3 && it in 0..255 } ?: return false }
                return b[0] == 10 || (b[0] == 172 && b[1] in 16..31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] in 64..127)
            }

            /**
             * Reads https://ersinkoc.github.io/phonebox/pair/#v=1&... (the QR code; the data is in the fragment so it
             * never reaches a web server) or phonebox://pair?v=1&... (the "Open in PhoneBox" button on that page).
             * Anything malformed returns null.
             */
            fun parse(uri: Uri?): PairLink? {
                if (uri == null) return null
                val raw = when {
                    uri.scheme == "https" && uri.host == "ersinkoc.github.io" && uri.path?.startsWith("/phonebox/pair") == true -> uri.encodedFragment
                    uri.scheme == "phonebox" && uri.host == "pair" -> uri.encodedQuery
                    else -> null
                } ?: return null
                val q = raw.split('&').mapNotNull { part ->
                    val i = part.indexOf('=')
                    if (i <= 0) null else part.substring(0, i) to Uri.decode(part.substring(i + 1))
                }.toMap()
                if (q["v"] != "1") return null
                val fp = q["fp"]?.lowercase()?.takeIf { FINGERPRINT.matches(it) } ?: return null
                val port = q["port"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
                val code = q["c"]?.takeIf { CODE.matches(it) } ?: return null
                val hosts = q["h"].orEmpty().split(',').map { it.trim() }.filter { isLocalAddress(it) }.take(8)
                if (hosts.isEmpty()) return null
                val pc = q["pc"].orEmpty().filter { it >= ' ' }.take(64).ifBlank { "your computer" }
                return PairLink(pc, fp, port, hosts, code)
            }
        }
    }

    /**
     * Trades the link's one-time code for the Wi-Fi key and makes that computer the active one. Blocks (network):
     * call it off the main thread. Returns null when paired, otherwise a message for the user.
     */
    fun claim(link: PairLink): String? {
        val pinned = pinnedFactory(link.fingerprint)
        var problem = "Could not reach ${link.pc} at ${link.hosts.joinToString()}. The phone and the computer must be " +
            "on the same network (or both on Tailscale), and the computer's firewall must allow PhoneBox."
        for (host in link.hosts) {
            try {
                val c = URL("https://$host:${link.port}/api/pair/claim").openConnection() as HttpsURLConnection
                c.sslSocketFactory = pinned
                c.hostnameVerifier = pinnedHostnameVerifier // identity is the pinned certificate
                c.connectTimeout = 3000
                c.readTimeout = 8000
                c.requestMethod = "POST"
                c.doOutput = true
                c.setRequestProperty("Content-Type", "application/json")
                c.setRequestProperty("X-PhoneBox-Model", deviceModel)
                c.outputStream.use { it.write(JSONObject().put("code", link.code).toString().toByteArray()) }
                when (c.responseCode) {
                    200 -> {}
                    403 -> return "${link.pc} did not accept the code: it works once and for 5 minutes. Start pairing again on the computer."
                    else -> { problem = "${link.pc} answered ${c.responseCode}."; continue }
                }
                val j = JSONObject(c.inputStream.bufferedReader().readText())
                if (!j.optString("fingerprint").equals(link.fingerprint, ignoreCase = true)) return "${link.pc} sent a different certificate. Start pairing again."
                val hosts = j.optJSONArray("hosts")?.let { a -> (0 until a.length()).map { a.getString(it) } }
                    ?.filter { PairLink.isLocalAddress(it) }?.takeIf { it.isNotEmpty() } ?: link.hosts
                // the name the user confirmed; only an entry for this very certificate is replaced, never one that
                // merely has the same name
                val pc = Pc(link.fingerprint.lowercase(), link.pc, hosts, j.optInt("port", link.port), j.getString("token"))
                synchronized(pcsLock) {
                    savePcs(pcs.filter { it.id != pc.id } + pc, pc.id)
                    factory = null
                    preferredHost = 0
                }
                return null
            } catch (e: javax.net.ssl.SSLException) {
                problem = "The computer at $host has a different certificate than the QR code. Start pairing again."
            } catch (e: Exception) {
                // try the next address
            }
        }
        return problem
    }

    private fun wrapTlsFactory(delegate: SSLSocketFactory): SSLSocketFactory = object : SSLSocketFactory() {
        private fun tune(s: Socket): Socket {
            try { s.tcpNoDelay = true } catch (_: Exception) {}
            try { s.keepAlive = true } catch (_: Exception) {}
            tlsSocketCount.incrementAndGet()
            return s
        }
        override fun getDefaultCipherSuites(): Array<String> = delegate.defaultCipherSuites
        override fun getSupportedCipherSuites(): Array<String> = delegate.supportedCipherSuites
        override fun createSocket(s: Socket, host: String, port: Int, autoClose: Boolean): Socket =
            tune(delegate.createSocket(s, host, port, autoClose))
        override fun createSocket(host: String, port: Int): Socket = tune(delegate.createSocket(host, port))
        override fun createSocket(host: String, port: Int, localHost: InetAddress, localPort: Int): Socket =
            tune(delegate.createSocket(host, port, localHost, localPort))
        override fun createSocket(host: InetAddress, port: Int): Socket = tune(delegate.createSocket(host, port))
        override fun createSocket(address: InetAddress, port: Int, localAddress: InetAddress, localPort: Int): Socket =
            tune(delegate.createSocket(address, port, localAddress, localPort))
        override fun createSocket(): Socket = tune(delegate.createSocket())
    }

    private fun pinnedFactory(pin: String): SSLSocketFactory {
        val tm = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = throw CertificateException()
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                val fp = MessageDigest.getInstance("SHA-256").digest(chain[0].encoded).joinToString("") { "%02x".format(it) }
                if (!fp.equals(pin, ignoreCase = true)) throw CertificateException("certificate does not match the paired PC")
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
        }
        val base = SSLContext.getInstance("TLS").apply { init(null, arrayOf(tm), SecureRandom()) }.socketFactory
        return wrapTlsFactory(base)
    }

    companion object {
        private const val USB_BASE = "http://127.0.0.1:8765"
        private const val REMOTE_READ_TIMEOUT = 1000
        /** The agent sends "state" every second, so a silent connection for this long is dead. */
        private const val STREAM_READ_TIMEOUT = 8000
    }
}
