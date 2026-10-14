using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed class CrewJobService(IConfiguration configuration, IWebHostEnvironment environment, DurableStorage storage,
    IncidentStore incidents, CrewStore crews, WorkOrderCatalog catalog, TimeProvider time) : IDisposable
{
    private readonly string _root = DataPaths.Resolve(configuration, environment);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string PhotoDirectory => Path.Combine(TenantContext.DirectoryFor(_root), "crew-photos");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record PhotoFile(string TenantId, string JobId, CrewJobPhoto Photo, string DataBase64, string StateHash);
    public void Dispose() => _gate.Dispose();
    public async Task<CrewJobs> JobsAsync(CrewActor actor, CancellationToken ct) => new((await incidents.ListVerifiedAsync(ct))
        .Where(value => value.AssignedCrewId == actor.CrewId && value.Status != IncidentStatus.Closed).OrderBy(value => value.Priority == "Urgent" ? 0 : 1)
        .ThenBy(value => value.CreatedAt).Select(View).ToArray(), time.GetUtcNow());
    public async Task<CrewJob?> JobAsync(CrewActor actor, string id, CancellationToken ct)
    { var incident = await incidents.GetVerifiedAsync(id, ct); return incident?.AssignedCrewId == actor.CrewId ? View(incident) : null; }
    public async Task<CrewJob?> ReviewJobAsync(string id, string actor, CancellationToken ct)
    {
        var incident = await incidents.GetVerifiedAsync(id, ct); if (incident is null) return null;
        await incidents.RecordSystemEventAsync(new("crew.job-evidence-reviewed", actor, "Named supervisor accessed tenant-local crew job evidence. Note/photo contents omitted from access audit.", time.GetUtcNow()), ct);
        return View(incident);
    }
    public Task<CrewJobResult> StatusAsync(CrewActor actor, string id, CrewJobStatusRequest request, CancellationToken ct)
    {
        if (request.Status is "paused" or "cannot-complete") CrewStore.Text(request.Reason, "reason", 2000, 5);
        else if (!string.IsNullOrEmpty(request.Reason)) CrewStore.Text(request.Reason, "reason", 2000, 1);
        if (request.Status is not ("en-route" or "on-site" or "paused" or "complete" or "cannot-complete")) throw new CrewRequestException(400, "Choose en-route, on-site, paused, complete or cannot-complete with a reason.");
        return MutateAsync(actor, id, request.OperationId, request.ExpectedVersion, request, "crew.job-status", candidate =>
        {
            if (candidate.Approval is null || candidate.NeedsHumanReview || candidate.Status is IncidentStatus.Closed or IncidentStatus.Resolved)
                return "A current human-approved active assignment is required before a crew work status update.";
            // Completion is a named worker report; the existing dispatcher resolve/draft/review workflow remains authoritative.
            var next = request.Status is "en-route" or "on-site" ? IncidentStatus.InProgress : candidate.Status;
            if (next != candidate.Status && IncidentWorkflow.Validate(candidate, next, actor.Name) is { } error) return error;
            candidate.Status = next; candidate.CrewWork!.Status = request.Status;
            candidate.Audit.Add(new("crew.job-status", actor.Name, "Named worker status: " + request.Status + ". Human reason: " + PiiRedactor.Redact(request.Reason), time.GetUtcNow()));
            return null;
        }, ct);
    }
    public Task<CrewJobResult> NoteAsync(CrewActor actor, string id, CrewJobNoteRequest request, CancellationToken ct)
    {
        CrewStore.Text(request.Text, "text", 4000, 1);
        return MutateAsync(actor, id, request.OperationId, request.ExpectedVersion, request, "crew.job-note", candidate =>
        {
            if (candidate.CrewWork!.Notes.Count >= 200) return "This job has reached its bounded note limit.";
            candidate.CrewWork.Notes.Add(new("note-" + CrewStore.Key(actor.WorkerId, request.OperationId)[..24], actor.WorkerId, PiiRedactor.Redact(request.Text), time.GetUtcNow()));
            candidate.Audit.Add(new("crew.job-note", actor.Name, "Named assigned worker added a purpose-limited job note; note content omitted from audit detail.", time.GetUtcNow())); return null;
        }, ct);
    }
    public async Task<CrewJobResult> PhotoAsync(CrewActor actor, string id, CrewJobPhotoRequest request, CancellationToken ct)
    {
        if (request.DataBase64 is null || request.DataBase64.Length > 2_800_000) throw new CrewRequestException(413, "Crew photos are limited to 2 MiB before metadata stripping.");
        byte[] supplied; try { supplied = Convert.FromBase64String(request.DataBase64); } catch (FormatException) { throw new CrewRequestException(400, "Photo must contain valid bounded base64."); }
        var bytes = CrewPhotoSanitizer.Strip(supplied, request.ContentType);
        var key = CrewStore.Key(actor.WorkerId + ":" + id, request.OperationId); var photoId = "photo-" + key[..24].ToLowerInvariant();
        var photo = new CrewJobPhoto(photoId, actor.WorkerId, request.ContentType, bytes.Length, request.LocationOptIn, true, time.GetUtcNow());
        var path = PhotoPath(photoId); var saved = false;
        try
        {
            return await MutateAsync(actor, id, request.OperationId, request.ExpectedVersion, request, "crew.job-photo", candidate =>
            {
                if (candidate.CrewWork!.Photos.Count >= 20) return "This job has reached its bounded photo limit.";
                candidate.CrewWork.Photos.Add(photo);
                candidate.Audit.Add(new("crew.job-photo", actor.Name, "Named assigned worker added equipment/hazard photo evidence. Server stripped metadata, including GPS, regardless of the client per-photo option; no photo bytes in audit.", time.GetUtcNow())); return null;
            }, ct, async () =>
            {
                var file = new PhotoFile(TenantContext.CurrentId, id, photo, Convert.ToBase64String(bytes), "");
                await storage.WriteAsync(path, JsonSerializer.Serialize(file with { StateHash = CrewStore.Hash(file) }, Json), ct); saved = true;
            });
        }
        catch { if (saved) await storage.DeleteAsync(path, ct); throw; }
    }
    public async Task<(byte[] Data, string ContentType)?> PhotoBytesAsync(CrewActor actor, string jobId, string photoId, CancellationToken ct)
    {
        if (!TenantContext.IsValidId(photoId)) return null;
        var job = await JobAsync(actor, jobId, ct); var policy = await crews.PolicyAsync(ct);
        if (job is null || !job.Photos.Any(value => value.Id == photoId && value.CreatedAt >= time.GetUtcNow().AddDays(-policy.RetentionDays))) return null;
        var persisted = await storage.ReadAsync(PhotoPath(photoId), ct); if (persisted is null) return null;
        var file = ReadPhoto(persisted); if (file.JobId != jobId) throw new InvalidDataException("Crew photo job mismatch.");
        await incidents.RecordSystemEventAsync(new("crew.photo-accessed", actor.Name, "Assigned worker accessed retained photo evidence. Photo data omitted from access audit.", time.GetUtcNow()), ct);
        return (Convert.FromBase64String(file.DataBase64), file.Photo.ContentType);
    }
    public async Task<(byte[] Data, string ContentType)?> ReviewPhotoBytesAsync(string jobId, string photoId, string actor, CancellationToken ct)
    {
        if (!TenantContext.IsValidId(photoId)) return null;
        var incident = await incidents.GetVerifiedAsync(jobId, ct); var policy = await crews.PolicyAsync(ct);
        if (incident?.CrewWork?.Photos.Any(value => value.Id == photoId && value.CreatedAt >= time.GetUtcNow().AddDays(-policy.RetentionDays)) is not true) return null;
        var persisted = await storage.ReadAsync(PhotoPath(photoId), ct); if (persisted is null) return null;
        var file = ReadPhoto(persisted); if (file.JobId != jobId) throw new InvalidDataException("Crew photo job mismatch.");
        await incidents.RecordSystemEventAsync(new("crew.photo-evidence-reviewed", actor, "Named supervisor accessed retained tenant-local sanitised photo evidence; no imagery or coordinates in access audit.", time.GetUtcNow()), ct);
        return (Convert.FromBase64String(file.DataBase64), file.Photo.ContentType);
    }
    public async Task<int> PurgePhotosAsync(CancellationToken ct)
    {
        var policy = await crews.PolicyAsync(ct); var cutoff = time.GetUtcNow().AddDays(-policy.RetentionDays); var removed = 0;
        await using var transaction = storage.Begin(); await transaction.InitializeAsync(ct); await _gate.WaitAsync(ct);
        try
        {
            var cases = await incidents.ListVerifiedAsync(ct);
            foreach (var path in await storage.ListPathsAsync(PhotoDirectory, ".json", ct))
            {
                var persisted = await storage.ReadAsync(path, ct); if (persisted is null) continue; var file = ReadPhoto(persisted);
                var incident = cases.FirstOrDefault(value => value.Id == file.JobId);
                if (file.Photo.CreatedAt >= cutoff && incident?.CrewWork?.Photos.Any(value => value.Id == file.Photo.Id) is true) continue;
                // Keep newly written, not-yet-referenced files for one hour so a concurrent atomic metadata write can finish.
                if (file.Photo.CreatedAt >= time.GetUtcNow().AddHours(-1) && incident?.CrewWork?.Photos.Any(value => value.Id == file.Photo.Id) is not true) continue;
                await storage.DeleteAsync(path, ct); removed++;
            }
            foreach (var incident in cases.Where(value => value.CrewWork?.Photos.Any(photo => photo.CreatedAt < cutoff) is true))
            {
                var result = await incidents.ChangeAsync(incident.Id, incident.Version, candidate =>
                {
                    candidate.CrewWork!.Photos.RemoveAll(photo => photo.CreatedAt < cutoff);
                    candidate.Audit.Add(new("crew.photo-retention-purged", "CivicOps automatic retention", "Expired crew photo metadata and GPS-free evidence bytes purged; no imagery retained in audit.", time.GetUtcNow())); return null;
                }, ct);
                if (result.Error is not null) throw new CrewRequestException(409, "Concurrent photo retention requires retry.");
            }
            return removed;
        }
        catch { transaction.Abort(); throw; } finally { _gate.Release(); }
    }
    private async Task<CrewJobResult> MutateAsync<T>(CrewActor actor, string id, string operationId, int expectedVersion, T request,
        string action, Func<Incident, string?> apply, CancellationToken ct, Func<Task>? beforeSave = null)
    {
        if (expectedVersion < 1) throw new CrewRequestException(400, "An authoritative job version is required.");
        var key = CrewStore.Key(actor.WorkerId, operationId); var hash = CrewStore.Hash(new { action, request });
        await using var transaction = storage.Begin(); await transaction.InitializeAsync(ct); await _gate.WaitAsync(ct);
        try
        {
            var incident = await incidents.GetVerifiedAsync(id, ct);
            if (incident is null || incident.AssignedCrewId != actor.CrewId) throw new CrewRequestException(404, "Assigned job is unavailable.");
            if (incident.CrewWork?.Receipts.TryGetValue(key, out var receipt) is true)
            {
                if (receipt.RequestHash != hash) throw new CrewRequestException(409, "This operation key already belongs to another job request.");
                return new(View(incident), true);
            }
            if (incident.Version != expectedVersion) throw new CrewRequestException(409, "This job changed. Review current status and deliberately resubmit the pending draft.");
            if (beforeSave is not null) await beforeSave();
            var changed = await incidents.ChangeAsync(id, expectedVersion, candidate =>
            {
                if (candidate.AssignedCrewId != actor.CrewId) return "Crew assignment changed; supervisor review is required.";
                candidate.CrewWork ??= new(); if (candidate.CrewWork.Receipts.Count >= 1000) return "The job operation limit is reached; ask a supervisor to review.";
                var error = apply(candidate); if (error is not null) return error;
                candidate.CrewWork.Receipts[key] = new(hash, time.GetUtcNow()); return null;
            }, ct);
            if (changed.Conflict) throw new CrewRequestException(409, changed.Error ?? "Job changed.");
            if (changed.Error is not null) throw new CrewRequestException(400, changed.Error);
            if (changed.Incident is null) throw new CrewRequestException(404, "Job unavailable.");
            return new(View(changed.Incident), false);
        }
        catch { transaction.Abort(); throw; } finally { _gate.Release(); }
    }
    private CrewJob View(Incident incident)
    {
        var type = catalog.Find(incident.WorkOrderTypeCode);
        return new(incident.Id, incident.ReferenceNumber, type?.Title ?? incident.Category, incident.Department, incident.Priority, incident.Status.ToString(),
            incident.CrewWork?.Status ?? "assigned", incident.Version,
            incident.LocationShared && incident.CreatedAt >= time.GetUtcNow() - GeoStore.ReportLocationRetention ? incident.Location : null,
            type?.HazardClass ?? "unknown", incident.Hazards, type?.Ppe ?? [], type?.RequiredCertifications ?? [], type?.ProcedureSteps ?? [],
            incident.CrewWork?.Notes ?? [], incident.CrewWork?.Photos ?? [], incident.Synthetic);
    }
    private string PhotoPath(string id) => Path.Combine(PhotoDirectory, id + ".json");
    private static PhotoFile ReadPhoto(string persisted)
    {
        var file = JsonSerializer.Deserialize<PhotoFile>(persisted, Json) ?? throw new InvalidDataException("Invalid crew photo.");
        if (file.TenantId != TenantContext.CurrentId || file.StateHash != CrewStore.Hash(file with { StateHash = "" })) throw new InvalidDataException("Crew photo integrity/municipality mismatch."); return file;
    }
}

/// <summary>Strips all JPEG metadata segments and PNG ancillary metadata without recognition or image inference.</summary>
public static class CrewPhotoSanitizer
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    public static byte[] Strip(byte[] data, string contentType)
    {
        if (data.Length is < 16 or > MaximumBytes) throw new CrewRequestException(data.Length > MaximumBytes ? 413 : 400, "Photo must be a bounded PNG/JPEG image of at most 2 MiB.");
        if (contentType == "image/png" && data.AsSpan().StartsWith(PngSignature)) return Png(data);
        if (contentType == "image/jpeg" && data[0] == 0xff && data[1] == 0xd8) return Jpeg(data);
        throw new CrewRequestException(400, "Photo MIME type and PNG/JPEG signature must match.");
    }
    private static byte[] Png(byte[] data)
    {
        using var output = new MemoryStream(); output.Write(PngSignature); var offset = 8; var header = false; var pixels = false; var end = false;
        while (offset + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            if (length > MaximumBytes || offset + 12L + length > data.Length) throw new CrewRequestException(400, "Malformed PNG chunk.");
            var size = checked((int)length); var kind = Encoding.ASCII.GetString(data, offset + 4, 4);
            if (!kind.All(char.IsAsciiLetter) || Crc(data.AsSpan(offset + 4, size + 4)) != BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + size + 8, 4)))
                throw new CrewRequestException(400, "PNG chunk integrity failed.");
            if (!header && (kind != "IHDR" || size != 13)) throw new CrewRequestException(400, "PNG image header required.");
            if (kind == "IHDR")
            {
                var width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8, 4)); var height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 12, 4));
                if (header || width is 0 or > 16384 || height is 0 or > 16384 || (ulong)width * height > 25_000_000) throw new CrewRequestException(400, "PNG dimensions must be bounded to25millionpixels."); header = true;
            }
            if (kind == "IDAT") pixels = true;
            if (kind == "IEND") { if (size != 0 || !pixels || offset + 12 != data.Length) throw new CrewRequestException(400, "Malformed PNG ending."); end = true; }
            // Keep only structural/pixel chunks and transparency. Strip eXIf/text/time/custom chunks, regardless of opt-in.
            if (kind is "IHDR" or "PLTE" or "IDAT" or "IEND" or "tRNS") output.Write(data, offset, size + 12);
            else if (char.IsUpper(kind[0])) throw new CrewRequestException(400, "Unsupported critical PNG chunk.");
            offset += size + 12;
        }
        if (!end || offset != data.Length) throw new CrewRequestException(400, "Complete PNG data required."); return output.ToArray();
    }
    private static byte[] Jpeg(byte[] data)
    {
        using var output = new MemoryStream(); output.Write(data, 0, 2); var offset = 2; var frame = false; var scan = false;
        while (offset + 1 < data.Length)
        {
            if (data[offset] != 0xff) throw new CrewRequestException(400, "Malformed JPEG marker.");
            var start = offset; while (offset < data.Length && data[offset] == 0xff) offset++; if (offset >= data.Length) break;
            var marker = data[offset++];
            if (marker == 0xd9) { if (!frame || !scan || offset != data.Length) throw new CrewRequestException(400, "Malformed JPEG ending."); output.WriteByte(0xff); output.WriteByte(0xd9); return output.ToArray(); }
            if (marker is 0 or 0xd8 or >= 0xd0 and <= 0xd7) throw new CrewRequestException(400, "Unexpected JPEG marker.");
            if (offset + 2 > data.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)); if (length < 2 || offset + length > data.Length) throw new CrewRequestException(400, "Malformed JPEG segment.");
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 8) throw new CrewRequestException(400, "JPEG frame is malformed.");
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 3, 2)); var width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 5, 2));
                if (width is 0 or > 16384 || height is 0 or > 16384 || width * height > 25_000_000)
                    throw new CrewRequestException(400, "JPEG dimensions must be bounded to25millionpixels."); frame = true;
            }
            // APP0–APP15 and COM are metadata; strip them all, including embedded thumbnails and EXIF GPS.
            if (marker is not (>= 0xe0 and <= 0xef) and not 0xfe) output.Write(data, start, offset + length - start);
            offset += length;
            if (marker != 0xda) continue;
            scan = true; var scanStart = offset;
            while (offset + 1 < data.Length)
            {
                if (data[offset] != 0xff) { offset++; continue; }
                var next = data[offset + 1]; if (next is 0 or >= 0xd0 and <= 0xd7) { offset += 2; continue; } break;
            }
            output.Write(data, scanStart, offset - scanStart);
        }
        throw new CrewRequestException(400, "Complete JPEG image required.");
    }
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (var value in data) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
        return ~crc;
    }
}
