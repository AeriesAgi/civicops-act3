using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CivicOps.Tests;

/// <summary>Release boundaries independent of either client's consent implementation.</summary>
public sealed class CrewClientPrivacyGuardTests
{
    private static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";
    private static string AndroidPath(params string[] parts) => Path.Combine(
        [SchemaContract.RepositoryRoot, "apps", "android-crew", "app", "src", .. parts]);

    [Fact]
    public void AndroidSourceManifestsCannotRequestContinuousTrackingOrIdentityCollection()
    {
        string[] allowed = ["android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE",
            "android.permission.ACCESS_COARSE_LOCATION", "android.permission.ACCESS_FINE_LOCATION",
            "android.permission.FOREGROUND_SERVICE", "android.permission.FOREGROUND_SERVICE_LOCATION",
            "android.permission.POST_NOTIFICATIONS"];
        var manifests = Directory.GetFiles(AndroidPath(), "AndroidManifest.xml", SearchOption.AllDirectories);
        Assert.NotEmpty(manifests);
        foreach (var file in manifests)
        {
            var document = XDocument.Load(file);
            var permissions = document.Descendants().Where(element => element.Name.LocalName.StartsWith("uses-permission", StringComparison.Ordinal));
            foreach (var permission in permissions)
                Assert.Contains((string?)permission.Attribute(Android + "name"), allowed);
            // Tracking must start from a worker's visible action, never a boot receiver.
            Assert.DoesNotContain(document.Descendants("action"), action =>
                (string?)action.Attribute(Android + "name") is "android.intent.action.BOOT_COMPLETED" or "android.intent.action.LOCKED_BOOT_COMPLETED");
        }
    }

    [Fact]
    public void LocationServiceIsPrivateAndSensitiveAppStateCannotEnterAndroidBackup()
    {
        var document = XDocument.Load(AndroidPath("main", "AndroidManifest.xml"));
        var application = Assert.Single(document.Descendants("application"));
        Assert.Equal("false", (string?)application.Attribute(Android + "allowBackup"));
        Assert.Equal("false", (string?)application.Attribute(Android + "fullBackupContent"));
        var location = Assert.Single(application.Elements("service"), service =>
            (string?)service.Attribute(Android + "foregroundServiceType") == "location");
        Assert.Equal("false", (string?)location.Attribute(Android + "exported"));
        var extraction = XDocument.Load(AndroidPath("main", "res", "xml", "data_extraction_rules.xml"));
        foreach (var transport in new[] { "cloud-backup", "device-transfer" })
        {
            var rule = Assert.Single(extraction.Descendants(transport));
            Assert.Contains(rule.Elements("exclude"), excluded =>
                (string?)excluded.Attribute("domain") == "root" && (string?)excluded.Attribute("path") == ".");
        }
    }

    [Fact]
    public void AndroidSosNoticeUsesDialLinksAndDoesNotStartTracking()
    {
        const string notice = "CivicOps SOS alerts your municipality's control room. In a life-threatening emergency always call 10111 (police), 10177 (ambulance/fire) or 112 from a cell phone.";
        var strings = File.ReadAllText(AndroidPath("main", "res", "values", "strings.xml"));
        var encoded = Regex.Match(strings, "name=\"sos_notice\">([^<]+)</string>").Groups[1].Value;
        var decoded = encoded.Replace("\\'", "'", StringComparison.Ordinal).Replace("&apos;", "'", StringComparison.Ordinal);
        Assert.Contains(notice, decoded, StringComparison.Ordinal);
        var activity = File.ReadAllText(AndroidPath("main", "java", "za", "civicops", "crew", "MainActivity.kt"));
        Assert.Contains("Intent.ACTION_DIAL", activity, StringComparison.Ordinal);
        Assert.Contains("tel:$number", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTION_CALL", activity, StringComparison.Ordinal);
        var repository = File.ReadAllText(AndroidPath("main", "java", "za", "civicops", "crew", "CrewRepository.kt"));
        var sos = repository.Split("suspend fun sos(", 2)[1].Split("suspend fun shareSosAddress", 2)[0];
        Assert.DoesNotContain("ShiftLocationService", sos, StringComparison.Ordinal);
        Assert.DoesNotContain("startForegroundService", sos, StringComparison.Ordinal);
        Assert.Contains("municipalRoot = true", sos, StringComparison.Ordinal);
        Assert.Contains("\"location\", JSONObject.NULL", sos, StringComparison.Ordinal);
    }
}
