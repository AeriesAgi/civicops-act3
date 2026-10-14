package za.civicops.crew

import org.junit.Assert.*
import org.junit.Test
import java.io.File

/** Architecture guards keep location visible and deny a future always-on permission/boot path. */
class NativePrivacyGuardTest {
    private val manifest get() = File("src/main/AndroidManifest.xml").readText()
    @Test fun `no background location permission`() { assertFalse(manifest.contains("ACCESS_BACKGROUND_LOCATION")) }
    @Test fun `no boot tracking restart receiver`() { assertFalse(manifest.contains("BOOT_COMPLETED")); assertFalse(manifest.contains("<receiver")) }
    @Test fun `workmanager may sync jobs after boot but cannot start location service`() {
        val worker = File("src/main/java/za/civicops/crew/CrewSyncWorker.kt").readText()
        assertFalse(worker.contains("ShiftLocationService")); assertFalse(worker.contains("startForegroundService")); assertFalse(worker.contains("LocationManager"))
    }
    @Test fun `tracking service never requests automatic sticky restart`() {
        val service = File("src/main/java/za/civicops/crew/ShiftLocationService.kt").readText()
        assertTrue(service.contains("START_NOT_STICKY")); assertFalse(service.contains("return START_STICKY")); assertFalse(service.contains("START_REDELIVER_INTENT"))
    }
    @Test fun `only non exported foreground location service is declared`() {
        val declaration = Regex("<service[^>]+>").find(manifest)?.value.orEmpty()
        assertTrue(declaration.contains("android:exported=\"false\"")); assertTrue(declaration.contains("android:foregroundServiceType=\"location\""))
    }
    @Test fun `private data cannot be backed up`() { assertTrue(manifest.contains("android:allowBackup=\"false\"")); assertTrue(manifest.contains("android:fullBackupContent=\"false\"")) }
    @Test fun `sos notice dials emergency numbers and does not start tracking`() {
        val strings = File("src/main/res/values/strings.xml").readText()
        val encoded = Regex("name=\"sos_notice\">([^<]+)</string>").find(strings)?.groupValues?.get(1) ?: error("sos_notice missing")
        val decoded = encoded.replace("\\'", "'").replace("&apos;", "'")
        assertTrue(decoded.contains("CivicOps SOS alerts your municipality's control room. In a life-threatening emergency always call 10111 (police), 10177 (ambulance/fire) or 112 from a cell phone."))
        val activity = File("src/main/java/za/civicops/crew/MainActivity.kt").readText()
        assertTrue(activity.contains("Intent.ACTION_DIAL")); assertTrue(activity.contains("tel:\$number")); assertFalse(activity.contains("ACTION_CALL"))
        val sos = File("src/main/java/za/civicops/crew/CrewRepository.kt").readText().substringAfter("suspend fun sos(").substringBefore("suspend fun shareSosAddress")
        assertFalse(sos.contains("ShiftLocationService")); assertFalse(sos.contains("startForegroundService")); assertTrue(sos.contains("municipalRoot = true"))
    }
    @Test fun `location state cannot be reauthorized by a default`() {
        val defaults = LocationConsentState(); assertFalse(defaults.sharingRequested); assertFalse(defaults.consentGranted); assertFalse(defaults.serverVerified)
    }
    @Test fun `all native source contracts and Room schema fields exclude identification and person scoring`() {
        val sources = File("src").walkTopDown().filter { file ->
            file.isFile && file.extension in setOf("kt", "java") &&
                file.relativeTo(File("src")).invariantSeparatorsPath.let { !it.startsWith("test/") && !it.startsWith("androidTest/") }
        }.toList()
        val schemas = File("schemas").walkTopDown().filter { it.isFile && it.extension == "json" }.toList()
        assertTrue("Release source contracts must be discovered without a type whitelist", sources.isNotEmpty())
        assertTrue("All committed Room schema versions must be discovered", schemas.isNotEmpty())
        val violations = sources.flatMap { file ->
            assertTrue("Bounded source scan: $file", file.length() <= 1_048_576)
            NativeContractGuard.sourceNames(file.readText()).filter(NativeContractGuard::prohibited).map { "$file: $it" }
        } + schemas.flatMap { file ->
            assertTrue("Bounded Room schema scan: $file", file.length() <= 2_097_152)
            val fields = NativeContractGuard.roomFieldNames(file.readText())
            assertTrue("Room fieldPath/columnName coverage: $file", fields.isNotEmpty())
            fields.filter(NativeContractGuard::prohibited).map { "$file: $it" }
        }
        assertTrue("Forbidden native contract/schema names: ${violations.joinToString()}", violations.isEmpty())
    }
    @Test fun `source guard detects newly added DTO properties and serialized aliases`() {
        val source = """
            data class FutureDto(val faceEmbedding: String, val workerThreatScore: Int)
            data class AnotherDto(val `voice-print`: String)
            enum class Observation { FACE_MATCH }
            val payload = JSONObject().put("biometric_template", "synthetic")
            @SerialName("gait\u0053ignature") val harmlessAlias: String
            interface CrewInterface
            class RoadSurface(val incidentRisk: Int)
            // val faceDetection is a prohibited example in a comment, not a declaration.
        """.trimIndent()
        val rawContract = "\nval rawContract = JSONObject(\"\"\"{\"faceTemplate\":\"synthetic\"}\"\"\")"
        val violations = NativeContractGuard.sourceNames(source + rawContract).filter(NativeContractGuard::prohibited).toSet()
        assertTrue(violations.containsAll(setOf("faceEmbedding", "workerThreatScore", "voice-print", "FACE_MATCH", "biometric_template", "gaitSignature", "faceTemplate")))
        assertFalse(violations.contains("faceDetection"))
        assertFalse(NativeContractGuard.prohibited("CrewInterface"))
        assertFalse(NativeContractGuard.prohibited("RoadSurface"))
        assertFalse(NativeContractGuard.prohibited("incidentRisk"))
    }
    @Test fun `Room guard traverses every field alias and rejects escaped identification names`() {
        val schema = """{"database":{"entities":[{"fields":[{"fieldPath":"safePayload","columnName":"face_embedding"},{"fieldPath":"biometricTemplate","columnName":"voice\u0050rint"}]}],"nested":{"fieldPath":"residentRiskScore","columnName":"licencePlatePersonLookup"}}}"""
        val fields = NativeContractGuard.roomFieldNames(schema)
        assertEquals(6, fields.size)
        assertFalse(NativeContractGuard.prohibited(fields.first()))
        assertTrue(fields.drop(1).all(NativeContractGuard::prohibited))
    }
}

/** Test-only lexical guard: scans every release identifier/serialized key, not selected reflected DTOs. */
private object NativeContractGuard {
    private val quoted = Regex("\"(?:\\\\.|[^\"\\\\])*\"")
    private val lexical = Regex("\"\"\"[\\s\\S]*?\"\"\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/")
    private val identifiers = Regex("`([^`]+)`|[\\p{L}_][\\p{L}\\p{N}_]*")
    fun sourceNames(source: String): List<String> {
        val serialized = mutableListOf<String>()
        val code = lexical.replace(source) { match ->
            val literal = match.value
            val names = when {
                literal.startsWith("\"\"\"") -> literal.substring(3, literal.length - 3).let { body -> listOf(body) + quoted.findAll(body).map { unquote(it.value) }.toList() }
                literal.startsWith('"') -> listOf(unquote(literal))
                else -> emptyList()
            }
            serialized += names.filter { it.matches(Regex("[\\p{L}_][\\p{L}\\p{N}_.-]*")) }
            " ".repeat(literal.length)
        }
        return identifiers.findAll(code).map { it.groups[1]?.value ?: it.value }.toList() + serialized
    }
    fun roomFieldNames(schema: String): List<String> {
        val strings = quoted.findAll(schema).toList()
        return strings.mapIndexedNotNull { index, key ->
            if (unquote(key.value) !in setOf("fieldPath", "columnName")) return@mapIndexedNotNull null
            val value = strings.getOrNull(index + 1) ?: error("Missing Room field value")
            require(schema.substring(key.range.last + 1, value.range.first).trim() == ":") { "Room field names must be JSON strings" }
            unquote(value.value)
        }
    }
    private fun unquote(value: String): String = Regex("\\\\(u[0-9a-fA-F]{4}|.)").replace(value.substring(1, value.length - 1)) { match ->
        val escape = match.groupValues[1]
        if (escape.startsWith('u') && escape.length == 5) escape.substring(1).toInt(16).toChar().toString()
        else when (escape) { "n" -> "\n"; "r" -> "\r"; "t" -> "\t"; "b" -> "\b"; "f" -> "\u000c"; else -> escape }
    }
    fun prohibited(name: String): Boolean {
        val compact = name.filter(Char::isLetterOrDigit).lowercase()
        val tokens = name.replace(Regex("([a-z0-9])([A-Z])"), "$1 $2")
            .replace(Regex("([A-Z])([A-Z][a-z])"), "$1 $2").lowercase().split(Regex("[^\\p{L}\\p{N}]+")).toSet()
        if (tokens.any { it in setOf("face", "faces", "facial", "gait", "embedding", "criminality", "suspicion", "suspicious") ||
                it.startsWith("biometric") || it.startsWith("iris") || it.startsWith("retina") || it.startsWith("fingerprint") }) return true
        if (listOf("faceembedding", "facerecognition", "facedetection", "facematch", "faceprint", "facetemplate", "voiceprint", "gaitsignature", "gaitembedding", "predictivecrime").any(compact::contains)) return true
        if (tokens.any { it in setOf("voice", "speaker") } && tokens.any { it in setOf("print", "identity", "identification", "recognition", "verification", "match", "signature", "template") }) return true
        if ("plate" in tokens && tokens.any { it in setOf("person", "people", "individual", "owner", "identity") }) return true
        if ("policing" in tokens && tokens.any { it in setOf("predictive", "prediction", "predicted") }) return true
        if (tokens.any { it in setOf("person", "people", "individual", "resident", "citizen", "worker", "suspect", "offender", "human") } &&
            tokens.any { it in setOf("risk", "threat", "criminality", "suspicion", "suspicious", "priority") }) return true
        return tokens.any { it in setOf("neighbourhood", "neighborhood", "ward", "district", "community", "precinct") } &&
            tokens.any { it in setOf("crime", "criminal") } && tokens.any { it in setOf("risk", "score", "rank", "priority", "forecast", "probability", "predictive", "prediction", "predicted") }
    }
}
