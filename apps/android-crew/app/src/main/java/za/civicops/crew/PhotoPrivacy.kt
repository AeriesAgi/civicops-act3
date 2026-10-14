package za.civicops.crew

import android.graphics.BitmapFactory
import androidx.exifinterface.media.ExifInterface
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.File

/** Re-encode pixels: no original EXIF, device id or timestamps enter the upload by default. */
object PhotoPrivacy {
    suspend fun prepare(source: File, locationOptIn: Boolean): ByteArray = withContext(Dispatchers.IO) {
        val clean = File(source.parentFile, "clean-${source.name}")
        try {
            val metadata = ExifInterface(source)
            val originalCoordinates = if (locationOptIn) metadata.latLong else null
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeFile(source.path, bounds)
            require(bounds.outWidth > 0 && bounds.outHeight > 0 && bounds.outWidth.toLong() * bounds.outHeight <= 50_000_000) { "Photo dimensions exceeded the safety limit." }
            val sample = Integer.highestOneBit((maxOf(bounds.outWidth, bounds.outHeight) / 1600).coerceAtLeast(1))
            val decoded = BitmapFactory.decodeFile(source.path, BitmapFactory.Options().apply { inSampleSize = sample }) ?: error("The photo could not be decoded.")
            val transform = android.graphics.Matrix().apply { postRotate(metadata.rotationDegrees.toFloat()); if (metadata.isFlipped) postScale(-1f, 1f) }
            val bitmap = android.graphics.Bitmap.createBitmap(decoded, 0, 0, decoded.width, decoded.height, transform, true)
            if (bitmap !== decoded) decoded.recycle()
            try {
                ByteArrayOutputStream().use { output -> bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG, 80, output); clean.writeBytes(output.toByteArray()) }
            } finally { bitmap.recycle() }
            if (originalCoordinates != null) ExifInterface(clean).apply { setLatLong(originalCoordinates[0], originalCoordinates[1]); saveAttributes() }
            clean.readBytes().also { require(it.size <= 2 * 1024 * 1024) { "Choose a smaller photo (maximum 2 MiB)." } }
        } finally { source.delete(); clean.delete() }
    }
}
