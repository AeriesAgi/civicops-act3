using Microsoft.Extensions.Configuration;
using CivicOps.Services;

namespace CivicOps.Tests;

public sealed class StartupFailureTests
{
    [Fact]
    public void DescribeIsOneLineAndExplainsUnwritableDataDirectory()
    {
        var line = StartupFailure.Describe(new System.Reflection.TargetInvocationException(new UnauthorizedAccessException("Access to the path '/app/Data/.civicops.lock' is denied.")));
        Assert.StartsWith("CIVICOPS STARTUP FAILED:", line);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("uid=1654", line);
    }

    [Fact]
    public void DescribeNamesConfigurationErrors()
    {
        var line = StartupFailure.Describe(new InvalidOperationException("CIVICOPS_X must be set.\nDetails"));
        Assert.Equal("CIVICOPS STARTUP FAILED: InvalidOperationException: CIVICOPS_X must be set. Details", line);
    }

    [Fact]
    public void ReadOnlyDataDirectoryLockReportsActionableConfigError()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var dir = Directory.CreateTempSubdirectory("civicops-ro-").FullName;
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var error = Assert.Throws<InvalidOperationException>(() => new DataDirectoryLease(new CivicOpsOptions { DataDirectory = dir }));
            Assert.Contains("uid=1654", error.Message);
        }
        finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); Directory.Delete(dir, true); }
    }
}

public sealed class SecretTrimTests
{
    private static IConfiguration Config(string value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ELEVENLABS_API_KEY"] = value }).Build();

    [Theory]
    [InlineData("synthetic-key\n")] [InlineData("synthetic-key\r\n")] [InlineData("  synthetic-key  ")]
    public void EnvironmentSecretTrailingNewlineIsTrimmed(string value) =>
        Assert.Equal("synthetic-key", SecretValues.Read(Config(value), "ELEVENLABS_API_KEY"));

    [Fact]
    public void EmbeddedControlCharacterNamesTheVariableNotTheValue()
    {
        var error = Assert.Throws<SecretConfigurationException>(() => SecretValues.Read(Config("synthetic\nvalue"), "ELEVENLABS_API_KEY"));
        Assert.Contains("ELEVENLABS_API_KEY", error.Message);
        Assert.DoesNotContain("synthetic", error.ToString());
    }

    [Fact]
    public void DescribeIncludesInnerMessages()
    {
        var line = StartupFailure.Describe(new InvalidOperationException("outer", new ArgumentException("inner cause")));
        Assert.Contains("outer <- inner cause", line);
    }
}
