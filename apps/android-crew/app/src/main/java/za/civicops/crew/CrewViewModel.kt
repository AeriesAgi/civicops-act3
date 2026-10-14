package za.civicops.crew

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

class CrewViewModel(private val repository: CrewDataSource) : ViewModel() {
    val state = repository.state
    private val feedback = MutableStateFlow("")
    val message: StateFlow<String> = feedback
    fun showMessage(text: String) { feedback.value = text }
    fun refresh() = action { repository.refresh(); feedback.value = "Jobs refreshed." }
    fun status(job: CrewJob, status: String, reason: String) = action {
        require(CrewConfiguration.validStatus(status, reason)) { "Provide a reason when paused or unable to complete." }
        repository.changeStatus(job, status, reason); feedback.value = if (status == "complete")
            "Crew completion queued; a dispatcher must review and resolve the case." else "Status queued. Sync conflicts require your review."
    }
    fun note(job: CrewJob, text: String) = action { repository.addNote(job, text); feedback.value = "Note queued for sync." }
    fun startShift() = action { repository.startShift(); feedback.value = "Shift verified. Location stays off until you consent and choose Share." }
    fun endShift() = action { repository.endShift(); feedback.value = "Location stopped. Shift end queued if offline." }
    fun consent(accepted: Boolean) = action { repository.consent(accepted); feedback.value = if (accepted) "Consent recorded; choose Share to enable location." else "Consent withdrawn. Location stopped immediately." }
    fun discard(id: String) = action { repository.discardConflict(id) }
    fun action(block: suspend () -> Unit) = viewModelScope.launch {
        runCatching { block() }.onFailure { feedback.value = it.message ?: "The request failed. Your queued work is retained." }
    }
}
