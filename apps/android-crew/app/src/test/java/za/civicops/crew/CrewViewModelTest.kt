package za.civicops.crew

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class CrewViewModelTest {
    private val dispatcher = StandardTestDispatcher()
    private val fake = FakeData()
    private lateinit var vm: CrewViewModel
    @Before fun setUp() { Dispatchers.setMain(dispatcher); vm = CrewViewModel(fake) }
    @After fun tearDown() { Dispatchers.resetMain() }
    @Test fun `refresh delegates to assigned work source`() = runTest(dispatcher) { vm.refresh(); advanceUntilIdle(); assertEquals("refresh", fake.last) }
    @Test fun `worker status update carries chosen job and human reason`() = runTest(dispatcher) { vm.status(job, "paused", "Unsafe water"); advanceUntilIdle(); assertEquals("status:job-1:paused:Unsafe water", fake.last) }
    @Test fun `invalid action never reaches server or outbox`() = runTest(dispatcher) { vm.status(job, "cannot-complete", ""); advanceUntilIdle(); assertEquals("", fake.last); assertTrue(vm.message.value.contains("reason")) }
    @Test fun `shift start does not claim automatic location collection`() = runTest(dispatcher) { vm.startShift(); advanceUntilIdle(); assertEquals("start", fake.last); assertTrue(vm.message.value.contains("Location stays off")) }
    @Test fun `end shift invokes immediate stop flow`() = runTest(dispatcher) { vm.endShift(); advanceUntilIdle(); assertEquals("end", fake.last); assertTrue(vm.message.value.contains("Location stopped")) }
    @Test fun `withdrawal delegates explicit false consent`() = runTest(dispatcher) { vm.consent(false); advanceUntilIdle(); assertEquals("consent:false", fake.last) }
    @Test fun `notes queue without changing dispatch approval`() = runTest(dispatcher) { vm.note(job, "Synthetic inspection note"); advanceUntilIdle(); assertEquals("note:job-1:Synthetic inspection note", fake.last) }
    private val job = CrewJob("job-1", "SYNTHETIC-1", "Synthetic work", "Roads", "Medium", "Assigned", "", 1, null, null, "Medium", emptyList(), emptyList(), emptyList(), true)
    private class FakeData : CrewDataSource {
        override val state = MutableStateFlow(CrewUiState())
        var last = ""
        override suspend fun refresh() { last = "refresh" }
        override suspend fun changeStatus(job: CrewJob, status: String, reason: String) { last = "status:${job.id}:$status:$reason" }
        override suspend fun addNote(job: CrewJob, text: String) { last = "note:${job.id}:$text" }
        override suspend fun startShift() { last = "start" }
        override suspend fun endShift() { last = "end" }
        override suspend fun consent(accepted: Boolean) { last = "consent:$accepted" }
        override suspend fun discardConflict(id: String) { last = "discard:$id" }
    }
}
