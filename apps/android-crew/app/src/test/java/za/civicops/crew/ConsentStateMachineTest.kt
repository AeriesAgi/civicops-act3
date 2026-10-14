package za.civicops.crew

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant

class ConsentStateMachineTest {
    private val now = Instant.parse("2026-10-09T08:00:00Z")
    private val allowed = LocationConsentState("shift-1", now.plusSeconds(3600), "consent-1", true, true, now.plusSeconds(7200), true)
    @Test fun `fresh verified active shift and explicit consent allows visible collection`() { assertTrue(allowed.mayCollect(now, true)) }
    @Test fun `no active shift rejects collection`() { assertFalse(allowed.copy(shiftId = null).mayCollect(now, true)) }
    @Test fun `missing consent rejects collection`() { assertFalse(allowed.copy(consentGranted = false).mayCollect(now, true)) }
    @Test fun `missing consent receipt rejects collection`() { assertFalse(allowed.copy(consentId = null).mayCollect(now, true)) }
    @Test fun `absent visible notification rejects collection`() { assertFalse(allowed.mayCollect(now, false)) }
    @Test fun `consent alone cannot start sensors without explicit share`() { assertFalse(allowed.copy(sharingRequested = false).mayCollect(now, true)) }
    @Test fun `offline unverified shift cannot collect`() { assertFalse(allowed.copy(serverVerified = false).mayCollect(now, true)) }
    @Test fun `maximum shift deadline rejects even if permission still granted`() { assertFalse(allowed.copy(shiftExpiresAt = now).mayCollect(now, true)) }
    @Test fun `expired crew token rejects collection`() { assertFalse(allowed.copy(tokenExpiresAt = now).mayCollect(now, true)) }
    @Test fun `withdrawal clears receipt and sharing request`() {
        val state = allowed.withdraw(); assertNull(state.consentId); assertFalse(state.consentGranted); assertFalse(state.mayCollect(now, true))
    }
    @Test fun `end shift clears active id and prevents stale revival`() { val state = allowed.endShift(); assertNull(state.shiftId); assertFalse(state.mayCollect(now, true)) }
    @Test fun `stop keeps consent but prevents sensor use`() { assertTrue(allowed.stopSharing().consentGranted); assertFalse(allowed.stopSharing().mayCollect(now, true)) }
    @Test fun `tenant interval is always bounded`() { assertEquals(15, CrewPolicy(intervalSeconds = 0).boundedIntervalSeconds); assertEquals(600, CrewPolicy(intervalSeconds = 3600).boundedIntervalSeconds) }
    @Test fun `default interval sixty seconds`() { assertEquals(60, CrewPolicy().boundedIntervalSeconds) }
    @Test fun `per photo choice cannot bypass off shift guard`() { assertFalse(allowed.endShift().mayAttachPhotoLocation(now, true)) }
    @Test fun `per photo choice cannot bypass missing consent`() { assertFalse(allowed.withdraw().mayAttachPhotoLocation(now, true)) }
    @Test fun `per photo choice cannot bypass unverified server state`() { assertFalse(allowed.copy(serverVerified = false).mayAttachPhotoLocation(now, true)) }
    @Test fun `per photo choice cannot bypass token expiry`() { assertFalse(allowed.copy(tokenExpiresAt = now).mayAttachPhotoLocation(now, true)) }
    @Test fun `valid photo consent must still be explicit for that photo`() { assertFalse(allowed.mayAttachPhotoLocation(now, false)) }
    @Test fun `verified active shift can share photo only without continuous tracking`() { assertTrue(allowed.stopSharing().mayAttachPhotoLocation(now, true)); assertFalse(allowed.stopSharing().mayCollect(now, true)) }
}
