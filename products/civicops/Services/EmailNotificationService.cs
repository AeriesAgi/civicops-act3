using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed record EmailStatus(
    bool Enabled,
    string Mode,
    bool SmtpConfigured,
    string From,
    string? NotifyTo);

public sealed record EmailReceipt(
    string Mode,
    string MessageId,
    bool Queued,
    bool Sent,
    string? Error);

public sealed class EmailNotificationService(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<EmailNotificationService> logger, TimeProvider? time = null, DurableStorage? persistence = null)
{
    private static readonly Action<ILogger, string, string, Exception?> DeliveryFailed = LoggerMessage.Define<string, string>(
        LogLevel.Warning, new EventId(4101, "EmailDeliveryFailed"), "Email delivery failed in {Mode} mode ({Type}).");
    private readonly DurableStorage _storage = persistence ?? new DurableStorage(configuration, environment);
    private readonly string _dataDirectory =
        DataPaths.Resolve(configuration, environment);

    public EmailStatus GetStatus()
    {
        var mode = (configuration["CIVICOPS_EMAIL_MODE"] ?? "outbox").Trim().ToLowerInvariant();
        if (mode is not ("outbox" or "smtp") || bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo) mode = "outbox";
        var from = ValidRecipient(configuration["CIVICOPS_EMAIL_FROM"]) ?? "civicops@example.invalid";
        var notifyTo = configuration["CIVICOPS_EMAIL_NOTIFY_TO"];
        var smtpConfigured = mode == "smtp"
            && ValidSetting(configuration["CIVICOPS_EMAIL_SMTP_HOST"], 253)
            && int.TryParse(configuration["CIVICOPS_EMAIL_SMTP_PORT"], out var port) && port is > 0 and <= 65535
            && ValidSetting(configuration["CIVICOPS_EMAIL_SMTP_USER"], 254)
            && ValidSetting(SecretValues.Read(configuration, "CIVICOPS_EMAIL_SMTP_PASSWORD"), 4096);
        if (mode == "smtp" && !smtpConfigured) mode = "outbox";
        return new(mode is "outbox" or "smtp", mode, smtpConfigured, from,
            string.IsNullOrWhiteSpace(notifyTo) ? null : notifyTo.Trim());
    }

    public async Task<EmailReceipt> NotifyAsync(Incident incident, string detail, CancellationToken ct)
    {
        var status = GetStatus();
        var recipient = ValidRecipient(incident.ContactEmail) ?? ValidRecipient(status.NotifyTo);
        var id = $"civicops-{Guid.NewGuid():N}";
        if (recipient is null)
            return new(status.Mode, id, false, false, "No validated recipient configured.");

        var body = $"Reference: {incident.ReferenceNumber}\nStatus: {incident.Status}\nArea: {incident.Area}\nDepartment: {incident.Department}\n\n{detail}";
        try
        {
            if (status.Mode == "smtp" && status.SmtpConfigured)
            {
                using var message = new MailMessage(status.From, recipient, $"CivicOps {incident.ReferenceNumber}", body);
                using var client = new SmtpClient(configuration["CIVICOPS_EMAIL_SMTP_HOST"], int.Parse(configuration["CIVICOPS_EMAIL_SMTP_PORT"] ?? "587", CultureInfo.InvariantCulture))
                {
                    EnableSsl = bool.TryParse(configuration["CIVICOPS_EMAIL_USE_SSL"], out var ssl) ? ssl : true,
                    Credentials = new NetworkCredential(configuration["CIVICOPS_EMAIL_SMTP_USER"], SecretValues.Read(configuration, "CIVICOPS_EMAIL_SMTP_PASSWORD"))
                };
                await client.SendMailAsync(message, ct);
                return new("smtp", id, false, true, null);
            }

            var outbox = Path.Combine(TenantContext.DirectoryFor(_dataDirectory), "email-outbox");

            var file = Path.Combine(outbox, $"{(time ?? TimeProvider.System).GetUtcNow():yyyyMMdd-HHmmssfff}-{id}.eml");
            var eml = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"From: {status.From}")
                .AppendLine(CultureInfo.InvariantCulture, $"To: {recipient}")
                .AppendLine(CultureInfo.InvariantCulture, $"Subject: CivicOps {incident.ReferenceNumber}")
                .AppendLine("MIME-Version: 1.0")
                .AppendLine("Content-Type: text/plain; charset=utf-8")
                .AppendLine()
                .Append(body)
                .ToString();
            await _storage.WriteAsync(file, eml, ct);
            return new("outbox", id, true, false, null);
        }
        catch (Exception ex)
        {
            DeliveryFailed(logger, status.Mode, ex.GetType().Name, null);
            return new(status.Mode, id, false, false, "Delivery failed; inspect server logs.");
        }
    }

    private static bool ValidSetting(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);
    private static string? ValidRecipient(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && !value.Contains('\r') && !value.Contains('\n') && new EmailAddressAttribute().IsValid(value.Trim())
            ? value.Trim()
            : null;
    public async Task<int> EraseCaseOutboxAsync(string referenceNumber, CancellationToken ct)
    {
        var directory = Path.Combine(TenantContext.DirectoryFor(_dataDirectory), "email-outbox");

        var removed = 0;
        foreach (var file in await _storage.ListPathsAsync(directory, ".eml", ct))
        {
            var content = await _storage.ReadAsync(file, ct) ?? "";
            if (!content.Contains("Subject: CivicOps " + referenceNumber + "\n", StringComparison.Ordinal)
                && !content.Contains("Subject: CivicOps " + referenceNumber + "\r\n", StringComparison.Ordinal)) continue;
            await _storage.DeleteAsync(file, ct); removed++;
        }
        return removed;
    }
}
