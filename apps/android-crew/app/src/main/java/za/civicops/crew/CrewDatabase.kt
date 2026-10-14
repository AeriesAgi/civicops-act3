package za.civicops.crew

import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.RoomDatabase

@Entity(tableName = "jobs")
data class CachedJob(@PrimaryKey val id: String, val json: String)
@Entity(tableName = "outbox")
data class OutboxRow(@PrimaryKey val id: String, val path: String, val method: String, val body: String,
    val state: String = "pending", val createdAt: Long = System.currentTimeMillis()) {
    fun item() = QueueItem(id, path, method, body, state, createdAt)
}
@Dao
interface CrewDao {
    @Query("SELECT * FROM jobs ORDER BY id") suspend fun jobs(): List<CachedJob>
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun putJobs(rows: List<CachedJob>)
    @Query("DELETE FROM jobs") suspend fun clearJobs()
    @Query("SELECT * FROM outbox ORDER BY CASE WHEN path='/consent' AND method='DELETE' THEN 0 WHEN path='/shifts/end' THEN 1 ELSE 2 END, createdAt, id") suspend fun outbox(): List<OutboxRow>
    @Insert suspend fun enqueue(row: OutboxRow)
    @Query("DELETE FROM outbox WHERE id=:id") suspend fun remove(id: String)
    @Query("UPDATE outbox SET state=:state WHERE id=:id") suspend fun mark(id: String, state: String)
    @Query("DELETE FROM outbox WHERE path='/locations'") suspend fun discardLocations()
    @Query("DELETE FROM outbox") suspend fun clearOutbox()
    @Query("DELETE FROM outbox WHERE createdAt<:cutoff") suspend fun purge(cutoff: Long)
}
@Database(entities = [CachedJob::class, OutboxRow::class], version = 1, exportSchema = true)
abstract class CrewDatabase : RoomDatabase() { abstract fun dao(): CrewDao }
