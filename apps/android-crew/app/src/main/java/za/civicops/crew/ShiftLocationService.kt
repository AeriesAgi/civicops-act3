package za.civicops.crew

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.PackageManager
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.Build
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Instant

/** Started by a visible activity after explicit user consent; never restarted by boot or WorkManager. */
class ShiftLocationService : Service(), LocationListener {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var locations: LocationManager
    private val repository get() = (application as CrewApplication).repository
    private var nextCaptureAt = 0L
    private var deadlineElapsed = 0L
    private var foregroundVisible = false
    private var collectionBeganElapsed = 0L

    override fun onCreate() {
        super.onCreate()
        locations = getSystemService(LocationManager::class.java)
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(CHANNEL, getString(R.string.location_notification_channel), NotificationManager.IMPORTANCE_LOW))
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_WITHDRAW) {
            repository.stopLocation()
            (application as CrewApplication).applicationScope.launch { runCatching { repository.consent(false) } }
            stopSelf()
            return START_NOT_STICKY
        }
        val gate = repository.state.value.location
        if (!gate.mayCollect(Instant.now(), true) || !permissionsAndNotification()) { stopSelf(); return START_NOT_STICKY }
        startForeground(410, notification())
        foregroundVisible = true
        collectionBeganElapsed = SystemClock.elapsedRealtime()
        deadlineElapsed = SystemClock.elapsedRealtime() + minOf(
            java.time.Duration.between(Instant.now(), gate.shiftExpiresAt).toMillis(), gate.policy.maxShiftHours * 60L * 60 * 1000)
        val provider = if (gate.policy.accuracyMeters <= 50 || !locations.isProviderEnabled(LocationManager.NETWORK_PROVIDER)) LocationManager.GPS_PROVIDER else LocationManager.NETWORK_PROVIDER
        try {
            locations.requestLocationUpdates(provider, gate.policy.boundedIntervalSeconds * 1000L, 0f, this, Looper.getMainLooper())
        } catch (_: SecurityException) { repository.stopLocation(); stopSelf(); return START_NOT_STICKY }
        scope.launch {
            while (foregroundVisible) {
                if (SystemClock.elapsedRealtime() >= deadlineElapsed || !permissionsAndNotification() ||
                    !repository.state.value.location.mayCollect(Instant.now(), true)) {
                    repository.stopLocation()
                    stopSelf()
                    break
                }
                delay(1000)
            }
        }
        return START_NOT_STICKY
    }
    private fun permissionsAndNotification(): Boolean {
        val fine = ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED
        val coarse = ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_COARSE_LOCATION) == PackageManager.PERMISSION_GRANTED
        val notifications = Build.VERSION.SDK_INT < 33 || ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        return (fine || coarse) && notifications && NotificationManagerCompat.from(this).areNotificationsEnabled() &&
            getSystemService(NotificationManager::class.java).getNotificationChannel(CHANNEL)?.importance != NotificationManager.IMPORTANCE_NONE
    }
    private fun notification(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop = PendingIntent.getService(this, 1, Intent(this, ShiftLocationService::class.java).setAction(ACTION_WITHDRAW), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return NotificationCompat.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.ic_menu_mylocation)
            .setContentTitle(getString(R.string.app_name)).setContentText(getString(R.string.sharing_notification))
            .setStyle(NotificationCompat.BigTextStyle().bigText(getString(R.string.sharing_notification)))
            .setOngoing(true).setContentIntent(open).addAction(0, getString(R.string.withdraw_consent), stop).build()
    }
    override fun onLocationChanged(location: Location) {
        val now = SystemClock.elapsedRealtime()
        val gate = repository.state.value.location
        if (!gate.mayCollect(Instant.now(), foregroundVisible) || now >= deadlineElapsed || !permissionsAndNotification()) { repository.stopLocation(); stopSelf(); return }
        if (now < nextCaptureAt || !location.hasAccuracy()) return
        nextCaptureAt = now + gate.policy.boundedIntervalSeconds * 1000L
        // Reject stale provider fixes and derive capture time locally; clock changes cannot extend service lifetime.
        val fixElapsed = location.elapsedRealtimeNanos / 1_000_000L
        if (fixElapsed < collectionBeganElapsed || now - fixElapsed !in 0L..120_000L) return
        scope.launch { runCatching { repository.captureLocation(location.latitude, location.longitude, location.accuracy, Instant.now()) } }
    }
    override fun onProviderDisabled(provider: String) { repository.stopLocation(); stopSelf() }
    override fun onDestroy() {
        foregroundVisible = false
        if (::locations.isInitialized) locations.removeUpdates(this)
        repository.stopLocation()
        scope.cancel()
        stopForeground(STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }
    override fun onBind(intent: Intent?): IBinder? = null
    companion object { const val CHANNEL = "crew-on-shift-location"; const val ACTION_WITHDRAW = "za.civicops.crew.WITHDRAW" }
}
