package za.civicops.crew

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant

class OutboxPolicyTest {
    @Test fun `successful replay removes acknowledged item`() { assertEquals("acknowledged", OutboxPolicy.disposition(200)); assertEquals("acknowledged", OutboxPolicy.disposition(201)) }
    @Test fun `optimistic conflict never silently overwrites`() { assertEquals("conflict", OutboxPolicy.disposition(409)) }
    @Test fun `revoked session requires sign in`() { assertEquals("sign-in-required", OutboxPolicy.disposition(401)) }
    @Test fun `privacy rejection is not retried`() { assertEquals("rejected", OutboxPolicy.disposition(403)) }
    @Test fun `transient errors retry with bounded exponential backoff`() {
        listOf(408, 429, 500, 503).forEach { assertEquals("retry", OutboxPolicy.disposition(it)) }
        assertEquals(30L, OutboxPolicy.retryDelaySeconds(0)); assertEquals(60L, OutboxPolicy.retryDelaySeconds(1)); assertTrue(OutboxPolicy.retryDelaySeconds(100) <= 21600L)
    }
    @Test fun `off shift location drafts cannot replay`() { assertFalse(OutboxPolicy.mayReplayLocation(QueueItem("op", "/locations", "POST", "{}"), LocationConsentState(), Instant.now())) }
    @Test fun `withdrawn consent cannot replay queued location`() {
        val now = Instant.now(); val state = LocationConsentState("s", now.plusSeconds(3600), "c", true, true, now.plusSeconds(3600), true)
        assertFalse(OutboxPolicy.mayReplayLocation(QueueItem("op", "/locations", "POST", "{}"), state.withdraw(), now))
    }
    @Test fun `old consent id is never reused after a new grant`() {
        val now = Instant.now(); val state = LocationConsentState("s", now.plusSeconds(3600), "new-consent", true, true, now.plusSeconds(3600), true)
        assertFalse(OutboxPolicy.validLocationReceipt(state, "s", "old-consent", now))
        assertTrue(OutboxPolicy.validLocationReceipt(state, "s", "new-consent", now))
    }
    @Test fun `old shift id is never reused on a new shift`() {
        val now = Instant.now(); val state = LocationConsentState("new-shift", now.plusSeconds(3600), "c", true, true, now.plusSeconds(3600), true)
        assertFalse(OutboxPolicy.validLocationReceipt(state, "old-shift", "c", now))
    }
    @Test fun `offline note remains eligible without location sharing`() { assertTrue(OutboxPolicy.mayReplayLocation(QueueItem("op", "/me/jobs/job-1/notes", "POST", "{}"), LocationConsentState(), Instant.now())) }
    @Test fun `production server must use HTTPS and contain no credentials`() {
        assertTrue(CrewConfiguration.validBaseUrl("https://municipality.example", false))
        assertFalse(CrewConfiguration.validBaseUrl("http://municipality.example", false))
        assertFalse(CrewConfiguration.validBaseUrl("https://user:password@municipality.example", false))
        assertFalse(CrewConfiguration.validBaseUrl("https://municipality.example?token=synthetic", false))
    }
    @Test fun `debug cleartext is confined to explicit local hosts`() {
        listOf("localhost", "127.0.0.1", "10.0.2.2").forEach { assertTrue(CrewConfiguration.validBaseUrl("http://$it:5080", true)) }
        assertFalse(CrewConfiguration.validBaseUrl("http://public.example", true))
    }
    @Test fun `paused and cannot complete require human reason`() { assertFalse(CrewConfiguration.validStatus("paused", "")); assertFalse(CrewConfiguration.validStatus("cannot-complete", "")); assertTrue(CrewConfiguration.validStatus("paused", "Unsafe conditions")) }
    @Test fun `unrecognized status cannot become dispatch action`() { assertFalse(CrewConfiguration.validStatus("approve-and-dispatch", "request")) }
}
