package za.civicops.crew

import java.net.URI
import java.time.Instant

data class CrewPolicy(val intervalSeconds: Int = 60, val accuracyMeters: Int = 100, val retentionDays: Int = 30, val maxShiftHours: Int = 12) {
    val boundedIntervalSeconds get() = intervalSeconds.coerceIn(15, 600)
}

data class LocationConsentState(
    val shiftId: String? = null,
    val shiftExpiresAt: Instant? = null,
    val consentId: String? = null,
    val consentGranted: Boolean = false,
    val sharingRequested: Boolean = false,
    val tokenExpiresAt: Instant? = null,
    val serverVerified: Boolean = false,
    val policy: CrewPolicy = CrewPolicy()
) {
    fun onShift(now: Instant) = shiftId != null && shiftExpiresAt?.isAfter(now) == true
    fun mayCollect(now: Instant, foregroundVisible: Boolean) = onShift(now) && consentGranted && consentId != null &&
        sharingRequested && serverVerified && tokenExpiresAt?.isAfter(now) == true && foregroundVisible
    fun mayAttachPhotoLocation(now: Instant, explicitPhotoChoice: Boolean) = onShift(now) && consentGranted && consentId != null &&
        serverVerified && tokenExpiresAt?.isAfter(now) == true && explicitPhotoChoice
    fun stopSharing() = copy(sharingRequested = false)
    fun withdraw() = copy(consentId = null, consentGranted = false, sharingRequested = false, serverVerified = false)
    fun endShift() = copy(shiftId = null, shiftExpiresAt = null, sharingRequested = false, serverVerified = false)
}

data class CrewJob(val id: String, val reference: String, val title: String, val department: String, val priority: String,
    val status: String, val crewStatus: String, val version: Int, val latitude: Double?, val longitude: Double?,
    val hazardClass: String, val ppe: List<String>, val certifications: List<String>, val steps: List<String>, val synthetic: Boolean,
    val accountTag: String = "", val hazards: List<String> = emptyList(), val notes: List<String> = emptyList(), val photoCount: Int = 0)

data class QueueItem(val id: String, val path: String, val method: String, val body: String, val state: String = "pending", val createdAt: Long = System.currentTimeMillis())
data class SosReceipt(val id: String?, val message: String)
data class CrewUiState(val signedIn: Boolean = false, val jobs: List<CrewJob> = emptyList(), val queue: List<QueueItem> = emptyList(),
    val location: LocationConsentState = LocationConsentState(), val privacyNotice: String = "", val noticeVersion: String = "",
    val workerId: String = "", val demoMode: Boolean = false, val message: String = "", val busy: Boolean = false)

object OutboxPolicy {
    const val MAX_ROWS = 100
    const val MAX_BYTES = 20 * 1024 * 1024
    const val MAX_AGE_MILLIS = 7L * 24 * 60 * 60 * 1000
    fun disposition(httpStatus: Int) = when (httpStatus) {
        in 200..299 -> "acknowledged"
        408, 429, in 500..599 -> "retry"
        401 -> "sign-in-required"
        409 -> "conflict"
        else -> "rejected"
    }
    fun mayReplayLocation(item: QueueItem, state: LocationConsentState, now: Instant): Boolean =
        item.path != "/locations" || state.mayCollect(now, true)
    fun validLocationReceipt(state: LocationConsentState, shiftId: String?, consentId: String?, now: Instant): Boolean =
        state.mayCollect(now, true) && shiftId == state.shiftId && consentId == state.consentId
    fun retryDelaySeconds(attempt: Int) = (30L * (1L shl attempt.coerceIn(0, 8))).coerceAtMost(6 * 60 * 60)
}

object CrewConfiguration {
    fun validBaseUrl(value: String, debug: Boolean): Boolean = runCatching {
        val uri = URI(value)
        uri.host != null && uri.userInfo == null && uri.query == null && uri.fragment == null &&
            (uri.path.isNullOrEmpty() || uri.path == "/") &&
            (uri.scheme == "https" || debug && uri.scheme == "http" && uri.host in setOf("localhost", "127.0.0.1", "10.0.2.2"))
    }.getOrDefault(false)
    val statuses = setOf("en-route", "on-site", "paused", "complete", "cannot-complete")
    fun validStatus(status: String, reason: String) = status in statuses && reason.length <= 1000 &&
        (status !in setOf("paused", "cannot-complete") || reason.trim().length >= 5)
}
