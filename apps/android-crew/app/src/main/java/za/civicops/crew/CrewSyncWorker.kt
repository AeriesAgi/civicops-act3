package za.civicops.crew

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters

class CrewSyncWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        val repository = (applicationContext as CrewApplication).repository
        return runCatching { if (repository.sync()) Result.success() else Result.retry() }.getOrElse { Result.retry() }
    }
}
