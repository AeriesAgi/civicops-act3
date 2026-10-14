package za.civicops.crew

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URI
import java.io.ByteArrayOutputStream

data class ApiResult(val status: Int, val body: JSONObject)
class CrewApi(private val vault: CredentialVault) {
    suspend fun request(path: String, method: String = "GET", body: JSONObject? = null, authenticated: Boolean = true, municipalRoot: Boolean = false): ApiResult = withContext(Dispatchers.IO) {
        val base = vault.get("server") ?: error("Configure the municipality server in Settings.")
        require(CrewConfiguration.validBaseUrl(base, BuildConfig.DEBUG)) { "A secure municipality server is required." }
        require(path.startsWith("/") && !path.contains("..") && !path.contains("?"))
        if (municipalRoot) require(path == "/sos" || path.startsWith("/sos/")) { "Only the municipal SOS routes use the root prefix." }
        val prefix = if (municipalRoot) "/api/v1" else "/api/v1/crew"
        val connection = URI(base.trimEnd('/') + prefix + path).toURL().openConnection() as HttpURLConnection
        try {
            connection.requestMethod = method
            connection.connectTimeout = 15000
            connection.readTimeout = 15000
            connection.instanceFollowRedirects = false
            connection.setRequestProperty("Accept", "application/json")
            if (authenticated) connection.setRequestProperty("Authorization", "Bearer " + (vault.get("token") ?: error("Sign in first.")))
            if (body != null) {
                val bytes = body.toString().toByteArray(Charsets.UTF_8)
                require(bytes.size <= 3 * 1024 * 1024)
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json")
                connection.setFixedLengthStreamingMode(bytes.size)
                connection.outputStream.use { it.write(bytes) }
            }
            val status = connection.responseCode
            val stream = if (status in 200..299) connection.inputStream else connection.errorStream
            val text = stream?.use { input ->
                val output = ByteArrayOutputStream()
                val buffer = ByteArray(8192)
                var count = input.read(buffer)
                while (count >= 0) {
                    require(output.size() + count <= 4 * 1024 * 1024) { "Response exceeded limit." }
                    output.write(buffer, 0, count)
                    count = input.read(buffer)
                }
                val bytes = output.toByteArray()
                String(bytes, Charsets.UTF_8)
            }.orEmpty()
            ApiResult(status, if (text.isBlank()) JSONObject() else JSONObject(text))
        } finally { connection.disconnect() }
    }
}
