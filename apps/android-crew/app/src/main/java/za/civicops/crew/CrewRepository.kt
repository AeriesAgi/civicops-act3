package za.civicops.crew

import android.content.Context
import android.content.Intent
import androidx.room.Room
import androidx.room.withTransaction
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import java.util.UUID
import java.util.concurrent.TimeUnit

interface CrewDataSource {
    val state: StateFlow<CrewUiState>
    suspend fun refresh()
    suspend fun changeStatus(job: CrewJob, status: String, reason: String)
    suspend fun addNote(job: CrewJob, text: String)
    suspend fun startShift()
    suspend fun endShift()
    suspend fun consent(accepted: Boolean)
    suspend fun discardConflict(id: String)
}

class CrewRepository(private val context: Context) : CrewDataSource {
    val vault = CredentialVault(context)
    val api = CrewApi(vault)
    @Volatile private var visibleFix: Triple<Double, Double, Double>? = null
    private val database = Room.databaseBuilder(context, CrewDatabase::class.java, "crew-private.db").build()
    private val dao = database.dao()
    private val syncLock = Mutex()
    private val privacyLock = Mutex()
    private val reviewedConflicts = java.util.concurrent.ConcurrentHashMap<String, Int>()
    private val sessionGeneration = java.util.concurrent.atomic.AtomicLong(0)
    private val changes = MutableStateFlow(CrewUiState())
    override val state: StateFlow<CrewUiState> = changes

    suspend fun restore() = syncLock.withLock {
        withContext(Dispatchers.IO) {
            dao.purge(System.currentTimeMillis() - OutboxPolicy.MAX_AGE_MILLIS)
            if (!validSession()) { clearPrivateSession(); return@withContext }
            // A service must never resurrect after a process restart, reboot or restored backup.
            val stored = vault.get("locationState")?.let { parseLocation(JSONObject(it)) } ?: LocationConsentState()
            changes.value = changes.value.copy(signedIn = validSession(), location = stored.stopSharing().copy(serverVerified = false),
                workerId = vault.get("workerId").orEmpty())
            publishCache()
        }
    }

    fun server() = vault.get("server").orEmpty()
    suspend fun configureServer(value: String) {
        require(CrewConfiguration.validBaseUrl(value.trim(), BuildConfig.DEBUG)) { "Use HTTPS; debug builds permit local development hosts only." }
        signOut()
        vault.put("server", value.trim().trimEnd('/'))
    }
    fun validSession() = vault.get("token") != null && runCatching { Instant.parse(vault.get("expiresAt")).isAfter(Instant.now()) }.getOrDefault(false)

    suspend fun pair(pairingId: String, code: String, tenantId: String) {
        val response = api.request("/pairing/exchange", "POST", JSONObject().put("pairingId", pairingId).put("code", code).put("tenantId", tenantId), false)
        require(response.status in 200..299) { "Pairing failed or expired. Ask your supervisor for a new code." }
        installSession(response.body)
    }
    suspend fun installSession(response: JSONObject) {
        stopLocation()
        syncLock.withLock {
            clearPrivateSession()
            listOf("token", "expiresAt", "workerId", "tenantId", "deviceId").forEach { vault.put(it, response.getString(it)) }
            if (!validSession()) { clearPrivateSession(); error("The crew token is expired.") }
            changes.value = CrewUiState(signedIn = true, workerId = response.getString("workerId"))
        }
        refresh()
    }
    suspend fun signOut() {
        stopLocation()
        syncLock.withLock {
            if (validSession()) runCatching { api.request("/consent", "DELETE", operation()) }
            clearPrivateSession()
        }
    }

    override suspend fun refresh() = syncLock.withLock {
        publishCache()
        if (!validSession()) { stopLocation(); clearPrivateSession(); return@withLock }
        val me = api.request("/me")
        if (me.status !in 200..299) { handleSessionFailure(me.status); error("Unable to verify shift/consent. Location is stopped; cached jobs remain available.") }
        applyMe(me.body)
        val jobs = api.request("/me/jobs")
        if (jobs.status == 401 || jobs.status == 403) handleSessionFailure(jobs.status)
        require(jobs.status in 200..299) { "Unable to refresh jobs; use the offline cache." }
        val rows = jobs.body.getJSONArray("jobs").objects().map { CachedJob(it.getString("id"), it.toString()) }
        withContext(Dispatchers.IO) { database.withTransaction { dao.clearJobs(); dao.putJobs(rows) } }
        publishCache()
        scheduleSync()
    }

    override suspend fun changeStatus(job: CrewJob, status: String, reason: String) {
        requireJobScope(job)
        require(CrewConfiguration.validStatus(status, reason)) { "Select an allowed status and provide a reason when paused or unable to complete." }
        enqueue("/me/jobs/${job.id}/status", "POST", operation().put("status", status).put("reason", reason).put("expectedVersion", job.version), job.accountTag)
    }
    override suspend fun addNote(job: CrewJob, text: String) {
        requireJobScope(job)
        require(text.isNotBlank() && text.length <= 2000) { "A note must contain 1–2000 characters." }
        enqueue("/me/jobs/${job.id}/notes", "POST", operation().put("text", text).put("expectedVersion", job.version), job.accountTag)
    }
    suspend fun addPhoto(job: CrewJob, bytes: ByteArray, locationOptIn: Boolean) = privacyLock.withLock {
        requireJobScope(job)
        require(!locationOptIn || state.value.location.mayAttachPhotoLocation(Instant.now(), true)) { "Photo location requires an active verified shift and valid explicit consent. Capture again with location off." }
        require(bytes.size <= 2 * 1024 * 1024)
        enqueue("/me/jobs/${job.id}/photos", "POST", operation().put("contentType", "image/jpeg")
            .put("dataBase64", android.util.Base64.encodeToString(bytes, android.util.Base64.NO_WRAP))
            .put("locationOptIn", locationOptIn).put("expectedVersion", job.version), job.accountTag)
    }

    override suspend fun startShift() {
        // Local/offline start cannot authorize a sensor. /me verifies the accepted server shift.
        require(!withContext(Dispatchers.IO) { dao.outbox().any { it.path == "/shifts/end" } }) { "Sync your previous shift end before starting a new shift." }
        val response = api.request("/shifts/start", "POST", operation())
        require(response.status in 200..299) { "Connect to start a verified shift. Offline jobs and notes remain available." }
        vault.put("endedLocally", null)
        refresh()
    }
    override suspend fun endShift() = privacyLock.withLock {
        val shiftId = state.value.location.shiftId
        stopLocation()
        changes.update { it.copy(location = it.location.endShift()) }
        vault.put("endedLocally", "true")
        persistLocation()
        withContext(Dispatchers.IO) { dao.discardLocations() }
        if (shiftId != null) enqueue("/shifts/end", "POST", operation().put("shiftId", shiftId))
    }
    override suspend fun consent(accepted: Boolean) = privacyLock.withLock {
        if (!accepted) {
            stopLocation()
            changes.update { it.copy(location = it.location.withdraw()) }
            vault.put("withdrawnLocally", "true")
            persistLocation()
            withContext(Dispatchers.IO) { dao.discardLocations() }
            enqueue("/consent", "DELETE", operation())
        } else {
            require(!withContext(Dispatchers.IO) { dao.outbox().any { it.path == "/consent" && it.method == "DELETE" } }) { "Sync your consent withdrawal before granting new consent." }
            require(state.value.noticeVersion.isNotBlank()) { "Connect to read the current municipality privacy notice first." }
            val response = api.request("/consent", "POST", operation().put("noticeVersion", state.value.noticeVersion).put("accepted", true))
            require(response.status in 200..299) { "Consent was not accepted. Location remains off." }
            vault.put("withdrawnLocally", null)
            refresh()
        }
    }
    fun enableSharing() {
        changes.update {
            val gate = it.location.copy(sharingRequested = true)
            require(gate.mayCollect(Instant.now(), true)) { "A verified active shift and valid explicit consent are required." }
            it.copy(location = gate)
        }
        persistLocation()
    }
    fun stopLocation() {
        visibleFix = null
        changes.update { it.copy(location = it.location.stopSharing()) }
        persistLocation()
        context.stopService(Intent(context, ShiftLocationService::class.java))
        (context.applicationContext as CrewApplication).applicationScope.launch { withContext(Dispatchers.IO) { dao.discardLocations() } }
    }
    suspend fun captureLocation(latitude: Double, longitude: Double, accuracy: Float, capturedAt: Instant) = privacyLock.withLock {
        val gate = state.value.location
        if (!gate.mayCollect(Instant.now(), true)) return@withLock
        if (!accuracy.isFinite() || accuracy < 0 || accuracy > gate.policy.accuracyMeters) return@withLock
        visibleFix = Triple(latitude, longitude, accuracy.toDouble())
        enqueue("/locations", "POST", operation().put("shiftId", gate.shiftId).put("consentId", gate.consentId).put("visibleIndicator", true)
            .put("points", JSONArray().put(JSONObject().put("longitude", longitude).put("latitude", latitude)
                .put("accuracyMeters", accuracy.toDouble()).put("capturedAt", capturedAt.toString()))))
    }
    suspend fun ownHistory() = api.request("/me/locations/export").let {
        require(it.status in 200..299) { "Could not export your history." }; it.body.toString(2)
    }
    suspend fun sos(note: String, silent: Boolean, addressOrLandmark: String?): SosReceipt {
        // SOS never starts the location service. One already-visible fix may be attached; otherwise the worker shares a landmark.
        val gate = state.value.location
        val sharing = gate.mayCollect(Instant.now(), true)
        val body = operation().put("channel", "crew-android").put("note", note.take(1000))
            .put("contact", JSONObject.NULL).put("addressOrLandmark", addressOrLandmark ?: JSONObject.NULL)
            .put("silent", silent).put("location", JSONObject.NULL).put("accuracyMeters", JSONObject.NULL).put("batteryPercent", JSONObject.NULL)
        val response = api.request("/sos", "POST", body, municipalRoot = true)
        require(response.status in 200..299) { "SOS could not be recorded. No responder was notified. Use 10111, 10177 or 112 for life-threatening emergencies." }
        val alert = response.body.optJSONObject("alert")
        val id = alert?.optString("id")?.ifBlank { null }
        val point = if (sharing) visibleFix else null
        if (point != null && id != null) {
            val located = api.request("/sos/$id/location", "POST", operation()
                .put("location", JSONObject().put("longitude", point.first).put("latitude", point.second))
                .put("accuracyMeters", point.third).put("addressOrLandmark", JSONObject.NULL)
                .put("visibleIndicator", true).put("consentId", gate.consentId), municipalRoot = true)
            if (located.status !in 200..299) return SosReceipt(id, "SOS is active. The visible point was not accepted. Share a landmark. No new tracking was started.")
        }
        val message = response.body.optString("message", "SOS alert recorded for the municipal control room. A human decides any dispatch.")
        return SosReceipt(id, if (sharing && point != null) message else "$message Location sensors stay off unless visible sharing was already on. Share a landmark if you can.")
    }
    suspend fun shareSosAddress(id: String, addressOrLandmark: String): String {
        require(addressOrLandmark.isNotBlank() && addressOrLandmark.length <= 240)
        val response = api.request("/sos/$id/location", "POST", operation().put("location", JSONObject.NULL).put("accuracyMeters", JSONObject.NULL).put("addressOrLandmark", addressOrLandmark), municipalRoot = true)
        require(response.status in 200..299) { "The SOS location window is closed or the landmark was rejected." }
        return response.body.optString("message", "Landmark shared for this active SOS window only.")
    }
    suspend fun cancelSos(id: String): String {
        val response = api.request("/sos/$id/cancel", "POST", operation().put("note", "Crew member cancelled during the confirmation window."), municipalRoot = true)
        require(response.status in 200..299) { "The SOS cancellation window has ended." }
        return "SOS cancelled. No dispatch action was taken."
    }
    override suspend fun discardConflict(id: String) { withContext(Dispatchers.IO) { dao.remove(id) }; publishCache() }
    suspend fun reviewConflict(id: String) {
        val row = withContext(Dispatchers.IO) { dao.outbox().find { it.id == id && it.state == "conflict" } } ?: return
        val jobId = row.path.split('/').getOrNull(3) ?: return
        val result = api.request("/me/jobs/$jobId")
        require(result.status in 200..299) { "Could not load the latest assigned job." }
        val job = result.body.optJSONObject("job") ?: result.body
        withContext(Dispatchers.IO) { dao.putJobs(listOf(CachedJob(jobId, job.toString()))) }
        reviewedConflicts[id] = job.getInt("version")
        publishCache()
    }
    suspend fun retryConflict(id: String) {
        val row = withContext(Dispatchers.IO) { dao.outbox().find { it.id == id && it.state == "conflict" } } ?: return
        val reviewedVersion = reviewedConflicts.remove(id) ?: error("Load and review the latest job before confirming this retry.")
        val payload = JSONObject(row.body).put("operationId", UUID.randomUUID().toString()).put("expectedVersion", reviewedVersion)
        withContext(Dispatchers.IO) { dao.remove(id) }
        enqueue(row.path, row.method, payload)
    }

    private suspend fun enqueue(path: String, method: String, body: JSONObject, expectedAccount: String = accountTag()) {
        require(validSession()) { "Sign in to queue work for this municipality." }
        withContext(Dispatchers.IO) {
            require(expectedAccount == accountTag()) { "The active device identity changed. Refresh your assigned work." }
            dao.purge(System.currentTimeMillis() - OutboxPolicy.MAX_AGE_MILLIS)
            val existing = dao.outbox()
            require(existing.size < OutboxPolicy.MAX_ROWS && existing.sumOf { it.body.toByteArray().size } + body.toString().toByteArray().size <= OutboxPolicy.MAX_BYTES) { "Outbox capacity reached. Sync or review pending items first." }
            dao.enqueue(OutboxRow(body.getString("operationId"), path, method, body.toString()))
            if (expectedAccount != accountTag()) dao.remove(body.getString("operationId"))
            if (path == "/locations" && !OutboxPolicy.validLocationReceipt(state.value.location, body.optString("shiftId"), body.optString("consentId"), Instant.now())) dao.remove(body.getString("operationId"))
            if (path.endsWith("/photos") && body.optBoolean("locationOptIn") && !state.value.location.mayAttachPhotoLocation(Instant.now(), true)) dao.remove(body.getString("operationId"))
        }
        publishCache()
        scheduleSync()
    }
    private fun scheduleSync() {
        val request = OneTimeWorkRequestBuilder<CrewSyncWorker>().setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS).build()
        WorkManager.getInstance(context).enqueueUniqueWork("crew-outbox", ExistingWorkPolicy.APPEND_OR_REPLACE, request)
    }
    suspend fun sync(): Boolean = syncLock.withLock {
        withContext(Dispatchers.IO) { dao.purge(System.currentTimeMillis() - OutboxPolicy.MAX_AGE_MILLIS) }
        if (!validSession()) { stopLocation(); clearPrivateSession(); return@withLock true }
        val rows = withContext(Dispatchers.IO) { dao.outbox() }
        for (row in rows.filter { it.state == "pending" }) {
            val gate = state.value.location
            val body = JSONObject(row.body)
            if (row.path == "/locations" && !OutboxPolicy.validLocationReceipt(gate, body.optString("shiftId"), body.optString("consentId"), Instant.now())) {
                withContext(Dispatchers.IO) { dao.remove(row.id) }; continue
            }
            val result = runCatching { api.request(row.path, row.method, body) }.getOrNull() ?: return@withLock false
            if (result.status == 403 && row.path == "/locations") {
                stopLocation()
                changes.update { it.copy(location = it.location.copy(serverVerified = false)) }
                withContext(Dispatchers.IO) { dao.discardLocations() }
                continue
            }
            when (val outcome = OutboxPolicy.disposition(result.status)) {
                "acknowledged" -> withContext(Dispatchers.IO) {
                    result.body.optJSONObject("job")?.let { dao.putJobs(listOf(CachedJob(it.getString("id"), it.toString()))) }
                    dao.remove(row.id)
                }
                "retry" -> return@withLock false
                "sign-in-required" -> { stopLocation(); clearPrivateSession(); return@withLock true }
                "rejected" -> {
                    if (result.status == 403) { stopLocation(); clearPrivateSession(); return@withLock true }
                    withContext(Dispatchers.IO) { dao.mark(row.id, outcome) }
                }
                else -> withContext(Dispatchers.IO) { dao.mark(row.id, outcome) }
            }
        }
        publishCache()
        true
    }
    private suspend fun handleSessionFailure(status: Int) {
        stopLocation()
        if (status == 401 || status == 403) { clearPrivateSession(); return }
        changes.update { it.copy(location = it.location.copy(serverVerified = false), signedIn = status != 401 && validSession()) }
    }
    private suspend fun clearPrivateSession() {
        sessionGeneration.incrementAndGet()
        vault.clearSession()
        reviewedConflicts.clear()
        WorkManager.getInstance(context).cancelUniqueWork("crew-outbox")
        withContext(Dispatchers.IO) { dao.clearJobs(); dao.clearOutbox(); context.cacheDir.resolve("capture").deleteRecursively() }
        changes.value = CrewUiState()
    }
    private suspend fun publishCache() = withContext(Dispatchers.IO) {
        val generation = sessionGeneration.get()
        val account = accountTag()
        val jobs = dao.jobs().map { parseJob(JSONObject(it.json), account) }
        val queue = dao.outbox().map { it.item() }
        changes.update { if (generation == sessionGeneration.get() && account == accountTag()) it.copy(jobs = jobs, queue = queue) else it }
    }
    private fun accountTag() = vault.get("tenantId").orEmpty() + ":" + vault.get("workerId").orEmpty() + ":" + vault.get("deviceId").orEmpty()
    private fun requireJobScope(job: CrewJob) {
        require(job.accountTag.isNotEmpty() && job.accountTag == accountTag() && state.value.jobs.any { it.id == job.id }) { "This job belongs to a previous session. Refresh your assigned work." }
    }
    private fun applyMe(me: JSONObject) {
        val shift = me.optJSONObject("shift")
        val consent = me.optJSONObject("consent")
        val p = me.getJSONObject("policy")
        var next = LocationConsentState(
            shiftId = shift?.takeIf { it.isNull("endedAt") }?.optString("id"),
            shiftExpiresAt = shift?.optString("expiresAt")?.let { Instant.parse(it) },
            consentId = consent?.takeIf { it.isNull("withdrawnAt") }?.optString("id"),
            consentGranted = consent != null && consent.isNull("withdrawnAt"), sharingRequested = state.value.location.sharingRequested,
            tokenExpiresAt = Instant.parse(vault.get("expiresAt")), serverVerified = true,
            policy = CrewPolicy(p.getInt("intervalSeconds"), p.getInt("accuracyMeters"), p.getInt("retentionDays"), p.getInt("maxShiftHours")))
        if (vault.get("withdrawnLocally") == "true") next = next.withdraw()
        if (vault.get("endedLocally") == "true") next = next.endShift()
        changes.update { current ->
            var finalLocation = next.copy(sharingRequested = current.location.sharingRequested)
            if (vault.get("withdrawnLocally") == "true") finalLocation = finalLocation.withdraw()
            if (vault.get("endedLocally") == "true") finalLocation = finalLocation.endShift()
            current.copy(signedIn = true, workerId = me.getString("workerId"), location = finalLocation,
                privacyNotice = me.getString("privacyNotice"), noticeVersion = me.getString("noticeVersion"), demoMode = me.optBoolean("demoMode"))
        }
        if (!next.mayCollect(Instant.now(), true)) stopLocation()
        persistLocation()
    }
    private fun persistLocation() {
        val state = state.value.location
        vault.put("locationState", JSONObject().put("shiftId", state.shiftId).put("shiftExpiresAt", state.shiftExpiresAt?.toString())
            .put("consentId", state.consentId).put("consentGranted", state.consentGranted).toString())
    }
    private fun parseLocation(json: JSONObject) = LocationConsentState(
        shiftId = json.optString("shiftId").ifEmpty { null }, shiftExpiresAt = json.optString("shiftExpiresAt").ifEmpty { null }?.let { Instant.parse(it) },
        consentId = json.optString("consentId").ifEmpty { null }, consentGranted = json.optBoolean("consentGranted"),
        tokenExpiresAt = vault.get("expiresAt")?.let { Instant.parse(it) })
    private fun operation() = JSONObject().put("operationId", UUID.randomUUID().toString())
}

fun JSONArray.objects() = (0 until length()).map { getJSONObject(it) }
fun JSONArray.strings() = (0 until length()).map { getString(it) }
fun parseJob(json: JSONObject, accountTag: String = ""): CrewJob {
    val point = json.optJSONObject("location")
    return CrewJob(json.getString("id"), json.getString("referenceNumber"), json.getString("title"), json.getString("department"),
        json.getString("priority"), json.getString("status"), json.optString("crewStatus"), json.getInt("version"),
        point?.getDouble("latitude"), point?.getDouble("longitude"), json.getString("hazardClass"),
        json.getJSONArray("ppe").strings(), json.getJSONArray("requiredCertifications").strings(), json.getJSONArray("steps").strings(), json.optBoolean("synthetic"), accountTag,
        json.optJSONArray("hazards")?.strings() ?: emptyList(),
        json.optJSONArray("notes")?.objects()?.map { it.optString("text") } ?: emptyList(), json.optJSONArray("photos")?.length() ?: 0)
}
