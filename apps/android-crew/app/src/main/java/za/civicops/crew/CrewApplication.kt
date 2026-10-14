package za.civicops.crew

import android.app.Application
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class CrewApplication : Application() {
    val applicationScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    lateinit var repository: CrewRepository
        private set
    override fun onCreate() {
        super.onCreate()
        repository = CrewRepository(this)
        applicationScope.launch { repository.restore() }
    }
}
