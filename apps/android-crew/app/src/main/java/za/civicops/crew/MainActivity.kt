package za.civicops.crew

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.waitForUpOrCancellation
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import androidx.core.net.toUri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.viewmodel.compose.viewModel
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import net.openid.appauth.AuthorizationRequest
import net.openid.appauth.AuthorizationResponse
import net.openid.appauth.AuthorizationService
import net.openid.appauth.AuthorizationServiceConfiguration
import net.openid.appauth.ResponseTypeValues
import org.json.JSONObject
import java.io.File

class MainActivity : ComponentActivity() {
    private val repository get() = (application as CrewApplication).repository
    private lateinit var authorization: AuthorizationService
    private var photoFile: File? = null
    private var photoJob: CrewJob? = null
    private var photoLocationOptIn = false
    private var activityMessage by mutableStateOf("")

    private val authResult = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val data = result.data
        val response = data?.let { AuthorizationResponse.fromIntent(it) }
        if (response == null) { activityMessage = getString(R.string.sign_in_failed); return@registerForActivityResult }
        authorization.performTokenRequest(response.createTokenExchangeRequest()) { token, error ->
            lifecycleScope.launch {
                runCatching {
                    require(error == null && token?.idToken != null) { getString(R.string.sign_in_failed) }
                    val challenge = repository.vault.get("authChallenge") ?: error(getString(R.string.sign_in_failed))
                    val exchanged = repository.api.request("/auth/oidc", "POST", JSONObject().put("challengeId", challenge).put("idToken", token!!.idToken), false)
                    require(exchanged.status in 200..299) { getString(R.string.sign_in_failed) }
                    repository.installSession(exchanged.body)
                }.onFailure { activityMessage = it.message.orEmpty() }
                repository.vault.put("authChallenge", null)
            }
        }
    }
    private val locationPermissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        startSharingIfPermitted()
    }
    private val camera = registerForActivityResult(ActivityResultContracts.TakePicture()) { captured ->
        val file = photoFile
        val job = photoJob
        if (!captured || file == null || job == null) { file?.delete(); return@registerForActivityResult }
        lifecycleScope.launch {
            runCatching {
                val allowed = photoLocationOptIn && repository.state.value.location.mayAttachPhotoLocation(java.time.Instant.now(), true)
                repository.addPhoto(job, PhotoPrivacy.prepare(file, allowed), allowed)
            }
                .onSuccess { activityMessage = getString(R.string.photo_queued) }.onFailure { activityMessage = it.message.orEmpty() }
        }
    }
    private val exportHistory = registerForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri ->
        if (uri != null) lifecycleScope.launch {
            runCatching { val json = repository.ownHistory(); val output = contentResolver.openOutputStream(uri) ?: error("The selected export destination is unavailable."); output.use { it.write(json.toByteArray()) } }
                .onSuccess { activityMessage = getString(R.string.history_exported) }.onFailure { activityMessage = it.message.orEmpty() }
        }
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        authorization = AuthorizationService(this)
        if (savedInstanceState != null) {
            photoFile = savedInstanceState.getString("photoPath")?.let { File(it) }
            photoLocationOptIn = savedInstanceState.getBoolean("photoLocationOptIn")
        }
        setContent {
            MaterialTheme(colorScheme = darkColorScheme(primary = Color(0xFF79DDD1), background = Color(0xFF101A22), surface = Color(0xFF1A2833))) {
                Surface(Modifier.fillMaxSize()) {
                    val vm: CrewViewModel = viewModel(factory = object : ViewModelProvider.Factory {
                        @Suppress("UNCHECKED_CAST") override fun <T : ViewModel> create(modelClass: Class<T>) = CrewViewModel(repository) as T
                    })
                    CrewApp(vm, repository, activityMessage,
                        onOidc = { beginOidc() }, onShare = { requestSharing() }, onCapture = { job, optIn -> capture(job, optIn) },
                        onExport = { exportHistory.launch("civicops-my-location-history.json") })
                }
            }
        }
        lifecycleScope.launch { runCatching { repository.refresh() }.onFailure { activityMessage = it.message.orEmpty() } }
    }
    override fun onSaveInstanceState(outState: Bundle) {
        outState.putString("photoPath", photoFile?.path)
        outState.putBoolean("photoLocationOptIn", photoLocationOptIn)
        super.onSaveInstanceState(outState)
    }
    override fun onDestroy() { authorization.dispose(); super.onDestroy() }
    private fun beginOidc() = lifecycleScope.launch {
        runCatching {
            val config = repository.api.request("/auth/config", authenticated = false).body
            require(config.optBoolean("enabled")) { getString(R.string.oidc_disabled) }
            val challenge = repository.api.request("/auth/challenge", "POST", JSONObject(), false)
            require(challenge.status in 200..299) { getString(R.string.sign_in_failed) }
            repository.vault.put("authChallenge", challenge.body.getString("challengeId"))
            val authority = config.getString("authority").toUri()
            require(authority.scheme == "https") { getString(R.string.secure_issuer_required) }
            AuthorizationServiceConfiguration.fetchFromIssuer(authority) { discovered, exception ->
                if (exception != null || discovered == null) { activityMessage = getString(R.string.sign_in_failed); return@fetchFromIssuer }
                val request = AuthorizationRequest.Builder(discovered, config.getString("nativeClientId"), ResponseTypeValues.CODE,
                    "za.civicops.crew:/oauth2redirect".toUri())
                    .setScopes("openid", "profile").setNonce(challenge.body.getString("nonce")).build()
                // AppAuth creates an S256 PKCE verifier and uses the system browser / Custom Tabs.
                authResult.launch(authorization.getAuthorizationRequestIntent(request))
            }
        }.onFailure { activityMessage = it.message.orEmpty() }
    }
    private fun requestSharing() {
        val permissions = mutableListOf(Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION)
        if (Build.VERSION.SDK_INT >= 33) permissions += Manifest.permission.POST_NOTIFICATIONS
        locationPermissions.launch(permissions.toTypedArray())
    }
    private fun startSharingIfPermitted() {
        val location = ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED ||
            ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_COARSE_LOCATION) == PackageManager.PERMISSION_GRANTED
        val notification = Build.VERSION.SDK_INT < 33 || ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        if (!location || !notification) { activityMessage = getString(R.string.permission_required); repository.stopLocation(); return }
        runCatching {
            repository.enableSharing()
            ContextCompat.startForegroundService(this, Intent(this, ShiftLocationService::class.java))
        }.onFailure { repository.stopLocation(); activityMessage = it.message.orEmpty() }
    }
    private fun capture(job: CrewJob, optIn: Boolean) {
        val directory = cacheDir.resolve("capture").apply { mkdirs() }
        photoFile = File.createTempFile("crew-", ".jpg", directory)
        photoJob = job
        photoLocationOptIn = optIn && repository.state.value.location.mayAttachPhotoLocation(java.time.Instant.now(), true)
        camera.launch(FileProvider.getUriForFile(this, "$packageName.photos", photoFile!!))
    }
}

@Composable
private fun CrewApp(vm: CrewViewModel, repo: CrewRepository, activityMessage: String, onOidc: () -> Unit,
    onShare: () -> Unit, onCapture: (CrewJob, Boolean) -> Unit, onExport: () -> Unit) {
    val state by vm.state.collectAsState()
    val feedback by vm.message.collectAsState()
    var screen by rememberSaveable { mutableStateOf("jobs") }
    var selected by rememberSaveable { mutableStateOf<String?>(null) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(stringResource(R.string.app_name), style = MaterialTheme.typography.headlineMedium, modifier = Modifier.semantics { heading() })
        if (state.demoMode) Text(stringResource(R.string.synthetic_demo), color = MaterialTheme.colorScheme.primary)
        if (state.signedIn) {
            val onShift = state.location.onShift(java.time.Instant.now())
            val indicator = if (state.location.sharingRequested) stringResource(R.string.on_shift_sharing) else if (onShift) stringResource(R.string.on_shift_not_sharing) else stringResource(R.string.off_shift)
            Card(Modifier.fillMaxWidth()) { Text(indicator, Modifier.padding(16.dp).semantics { liveRegion = LiveRegionMode.Polite }, fontWeight = FontWeight.Bold) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(onClick = { screen = "jobs"; selected = null }, Modifier.weight(1f).heightIn(min = 48.dp)) { Text(stringResource(R.string.jobs)) }
                TextButton(onClick = { screen = "shift" }, Modifier.weight(1f).heightIn(min = 48.dp)) { Text(stringResource(R.string.shift)) }
                TextButton(onClick = { screen = "settings" }, Modifier.weight(1f).heightIn(min = 48.dp)) { Text(stringResource(R.string.settings)) }
            }
            Button(onClick = { screen = "sos" }, Modifier.fillMaxWidth().heightIn(min = 56.dp)) { Text(stringResource(R.string.sos_button), fontWeight = FontWeight.Bold) }
            when (screen) {
                "jobs" -> {
                    val job = state.jobs.find { it.id == selected }
                    if (job == null) JobsScreen(state, onRefresh = { vm.refresh() }, onJob = { selected = it.id })
                    else JobScreen(job, vm, state.location, onCapture)
                }
                "shift" -> ShiftScreen(state, vm, onShare, onStop = { repo.stopLocation() })
                "sos" -> SosScreen(vm, repo)
                else -> SettingsScreen(vm, repo, state, onExport)
            }
            if (state.queue.isNotEmpty()) OutboxScreen(state, vm, repo)
        } else SignInScreen(vm, repo, onOidc)
        if (feedback.isNotBlank()) Text(feedback, Modifier.semantics { liveRegion = LiveRegionMode.Polite })
        if (activityMessage.isNotBlank()) Text(activityMessage, Modifier.semantics { liveRegion = LiveRegionMode.Polite })
        Text(stringResource(R.string.collection_notice), style = MaterialTheme.typography.bodySmall)
    }
}

@Composable private fun SignInScreen(vm: CrewViewModel, repo: CrewRepository, onOidc: () -> Unit) {
    var server by remember { mutableStateOf(repo.server()) }
    var tenant by rememberSaveable { mutableStateOf("") }
    var pairingId by rememberSaveable { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    Heading(stringResource(R.string.sign_in))
    Text(stringResource(R.string.server_instruction))
    Field(server, { server = it }, stringResource(R.string.server_url), KeyboardType.Uri)
    Action(stringResource(R.string.save_server)) { vm.action { repo.configureServer(server) } }
    Action(stringResource(R.string.oidc_sign_in), onOidc)
    Text(stringResource(R.string.pairing_notice))
    Field(tenant, { tenant = it }, stringResource(R.string.tenant_id))
    Field(pairingId, { pairingId = it }, stringResource(R.string.pairing_id))
    Field(code, { code = it }, stringResource(R.string.pairing_code))
    Action(stringResource(R.string.pair_device)) { vm.action { repo.pair(pairingId, code.trim(), tenant.trim()); code = "" } }
}

@Composable private fun JobsScreen(state: CrewUiState, onRefresh: () -> Unit, onJob: (CrewJob) -> Unit) {
    var filter by rememberSaveable { mutableStateOf("") }
    Heading(stringResource(R.string.my_jobs))
    Field(filter, { filter = it }, stringResource(R.string.filter_jobs))
    Action(stringResource(R.string.refresh_jobs), onRefresh)
    val jobs = state.jobs.filter { "${it.reference} ${it.title} ${it.status} ${it.crewStatus}".contains(filter, true) }
    if (jobs.isEmpty()) Text(stringResource(R.string.no_jobs))
    jobs.forEach { job ->
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(job.reference, color = MaterialTheme.colorScheme.primary)
                Text(job.title, style = MaterialTheme.typography.titleLarge)
                Text("${job.department} · ${job.priority} · ${job.crewStatus.ifEmpty { job.status }}")
                if (job.synthetic) Text(stringResource(R.string.synthetic_job))
                Action(stringResource(R.string.open_job)) { onJob(job) }
            }
        }
    }
}

@Composable private fun JobScreen(job: CrewJob, vm: CrewViewModel, location: LocationConsentState, onCapture: (CrewJob, Boolean) -> Unit) {
    var reason by rememberSaveable(job.id) { mutableStateOf("") }
    var note by rememberSaveable(job.id) { mutableStateOf("") }
    var optIn by remember(job.id) { mutableStateOf(false) }
    Heading(job.title)
    Text("${job.reference} · ${job.crewStatus.ifEmpty { job.status }} · v${job.version}")
    JobMap(job)
    Heading(stringResource(R.string.hazards_ppe))
    Text(job.hazardClass)
    job.hazards.forEach { Text(it) }
    Text(stringResource(R.string.required_ppe, job.ppe.joinToString(", ")))
    Text(stringResource(R.string.required_certifications, job.certifications.joinToString(", ")))
    Text(stringResource(R.string.human_safety_notice))
    Heading(stringResource(R.string.procedure))
    job.steps.forEachIndexed { index, step -> Text("${index + 1}. $step") }
    Heading(stringResource(R.string.update_status))
    Field(reason, { reason = it.take(1000) }, stringResource(R.string.status_reason))
    listOf("en-route" to R.string.en_route, "on-site" to R.string.on_site, "paused" to R.string.paused, "complete" to R.string.complete, "cannot-complete" to R.string.cannot_complete).forEach { (status, label) ->
        Action(stringResource(label)) { vm.status(job, status, reason) }
    }
    Field(note, { note = it.take(2000) }, stringResource(R.string.note))
    Action(stringResource(R.string.save_note)) { vm.note(job, note); note = "" }
    if (job.notes.isNotEmpty()) { Heading(stringResource(R.string.existing_notes)); job.notes.forEach { Text(it) } }
    Text(stringResource(R.string.photo_count, job.photoCount))
    Text(stringResource(R.string.photo_notice))
    val photoConsentLabel = stringResource(R.string.photo_location_opt_in)
    val photoLocationAllowed = location.mayAttachPhotoLocation(java.time.Instant.now(), true)
    Row(Modifier.fillMaxWidth()) { Checkbox(optIn && photoLocationAllowed, { optIn = it && photoLocationAllowed }, Modifier.semantics { contentDescription = photoConsentLabel }, enabled = photoLocationAllowed); Text(photoConsentLabel, Modifier.padding(top = 12.dp)) }
    Action(stringResource(R.string.capture_photo)) { onCapture(job, optIn && photoLocationAllowed); optIn = false }
}

@Composable private fun JobMap(job: CrewJob) {
    val description = if (job.latitude != null) stringResource(R.string.map_coordinate, job.latitude, job.longitude ?: 0.0) else stringResource(R.string.no_coordinate)
    Box(Modifier.fillMaxWidth().height(160.dp).background(Color(0xFF152F3B)).semantics { contentDescription = description }) {
        Canvas(Modifier.fillMaxSize()) {
            for (index in 1..5) {
                drawLine(Color(0xFF294550), Offset(size.width * index / 6, 0f), Offset(size.width * index / 6, size.height))
                drawLine(Color(0xFF294550), Offset(0f, size.height * index / 6), Offset(size.width, size.height * index / 6))
            }
            if (job.latitude != null) drawCircle(Color(0xFF79DDD1), 12.dp.toPx(), center)
        }
    }
    Text(description)
    Text(stringResource(R.string.map_limit), style = MaterialTheme.typography.bodySmall)
}

@Composable private fun ShiftScreen(state: CrewUiState, vm: CrewViewModel, onShare: () -> Unit, onStop: () -> Unit) {
    var accepted by remember { mutableStateOf(false) }
    Heading(stringResource(R.string.shift_privacy))
    Text(state.privacyNotice.ifEmpty { stringResource(R.string.privacy_pending) })
    Text(stringResource(R.string.policy_summary, state.location.policy.boundedIntervalSeconds, state.location.policy.retentionDays, state.location.policy.maxShiftHours))
    Action(stringResource(R.string.start_shift)) { vm.startShift() }
    Action(stringResource(R.string.end_shift)) { vm.endShift() }
    val consentLabel = stringResource(R.string.explicit_consent)
    Row(Modifier.fillMaxWidth()) { Checkbox(accepted, { accepted = it }, Modifier.semantics { contentDescription = consentLabel }); Text(consentLabel, Modifier.padding(top = 12.dp)) }
    Button(onClick = { vm.consent(true); accepted = false }, enabled = accepted, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text(stringResource(R.string.record_consent)) }
    Action(stringResource(R.string.share_location), onShare)
    Action(stringResource(R.string.stop_sharing), onStop)
    Action(stringResource(R.string.withdraw_consent)) { vm.consent(false) }
}

@Composable private fun SosScreen(vm: CrewViewModel, repo: CrewRepository) {
    var note by rememberSaveable { mutableStateOf("") }
    var address by rememberSaveable { mutableStateOf("") }
    var silent by rememberSaveable { mutableStateOf(false) }
    var alertId by rememberSaveable { mutableStateOf<String?>(null) }
    var holding by remember { mutableStateOf(false) }
    val holdScope = rememberCoroutineScope()
    val silentLabel = stringResource(R.string.sos_silent)
    val context = LocalContext.current
    Heading(stringResource(R.string.sos_title))
    Text(stringResource(R.string.sos_notice), fontWeight = FontWeight.Bold)
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        listOf("10111" to R.string.sos_call_police, "10177" to R.string.sos_call_medical, "112" to R.string.sos_call_mobile).forEach { (number, label) ->
            TextButton(onClick = { context.startActivity(Intent(Intent.ACTION_DIAL, "tel:$number".toUri())) }, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                Text(stringResource(label))
            }
        }
    }
    Field(note, { note = it.take(1000) }, stringResource(R.string.sos_note))
    Field(address, { address = it.take(240) }, stringResource(R.string.sos_landmark))
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Checkbox(silent, { silent = it }, Modifier.semantics { contentDescription = silentLabel })
        Text(silentLabel, Modifier.padding(top = 12.dp))
    }
    Text(stringResource(R.string.sos_hold_help))
    Box(Modifier.pointerInput(Unit) {
        awaitEachGesture {
            awaitFirstDown(requireUnconsumed = false)
            holding = true
            val trigger = holdScope.launch {
                delay(3_000)
                holding = false
                vm.action {
                    val receipt = repo.sos(note, silent, address.trim().ifEmpty { null })
                    alertId = receipt.id
                    vm.showMessage(receipt.message)
                }
            }
            waitForUpOrCancellation()
            if (trigger.isActive) { trigger.cancel(); holding = false; vm.showMessage("SOS hold cancelled. No alert was sent.") }
        }
    }) {
        Button(onClick = {}, enabled = alertId == null, modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp)) {
            Text(if (holding) stringResource(R.string.sos_hold_progress) else stringResource(R.string.sos_hold_button), fontWeight = FontWeight.Bold)
        }
    }
    if (alertId != null) {
        Text(stringResource(R.string.sos_location_window), Modifier.semantics { liveRegion = LiveRegionMode.Polite })
        Action(stringResource(R.string.sos_cancel)) { vm.action { alertId?.let { id -> vm.showMessage(repo.cancelSos(id)); alertId = null } } }
        if (address.isNotBlank()) Action(stringResource(R.string.sos_share_landmark)) { vm.action { alertId?.let { id -> vm.showMessage(repo.shareSosAddress(id, address.trim())) } } }
    }
}

@Composable private fun SettingsScreen(vm: CrewViewModel, repo: CrewRepository, state: CrewUiState, onExport: () -> Unit) {
    Heading(stringResource(R.string.settings_privacy))
    Text(stringResource(R.string.worker_identity, state.workerId))
    Text(state.privacyNotice.ifEmpty { stringResource(R.string.privacy_pending) })
    Text(stringResource(R.string.storage_notice))
    Action(stringResource(R.string.export_history), onExport)
    Action(stringResource(R.string.sign_out)) { vm.action { repo.signOut() } }
}

@Composable private fun OutboxScreen(state: CrewUiState, vm: CrewViewModel, repo: CrewRepository) {
    Heading(stringResource(R.string.offline_outbox, state.queue.size))
    Text(stringResource(R.string.outbox_notice))
    state.queue.forEach { item ->
        Text("${item.method} ${item.path.substringAfterLast('/')} · ${item.state}")
        if (item.state != "pending") {
            Action(stringResource(R.string.discard_item)) { vm.discard(item.id) }
            if (item.state == "conflict") {
                val jobId = item.path.split('/').getOrNull(3)
                state.jobs.find { it.id == jobId }?.let { Text("${it.reference} · ${it.status} · ${it.crewStatus} · v${it.version}") }
                Text(JSONObject(item.body).optString("reason", JSONObject(item.body).optString("text", "")))
                Action(stringResource(R.string.load_conflict)) { vm.action { repo.reviewConflict(item.id) } }
                Action(stringResource(R.string.review_retry)) { vm.action { repo.retryConflict(item.id) } }
            }
        }
    }
}
@Composable private fun Heading(text: String) { Text(text, style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() }) }
@Composable private fun Field(value: String, onChange: (String) -> Unit, label: String, type: KeyboardType = KeyboardType.Text) {
    OutlinedTextField(value, onChange, label = { Text(label) }, modifier = Modifier.fillMaxWidth(), keyboardOptions = KeyboardOptions(keyboardType = type))
}
@Composable private fun Action(text: String, action: () -> Unit) { OutlinedButton(action, Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text(text) } }
