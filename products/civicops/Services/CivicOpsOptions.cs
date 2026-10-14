namespace CivicOps.Services;

public sealed class CivicOpsOptions
{
    public string DataDirectory { get; init; } = "";
    public bool DemoMode { get; init; }
    public int IntakeLimit { get; init; } = 60;
    public int DispatchLimit { get; init; } = 120;
    public int ToolLimit { get; init; } = 120;
    public int AuthLimit { get; init; } = 20;
    public static CivicOpsOptions From(IConfiguration config, IWebHostEnvironment env) => new()
    {
        DataDirectory = DataPaths.Resolve(config, env),
        DemoMode = bool.TryParse(config["CIVICOPS_DEMO_MODE"], out var demo) && demo,
        IntakeLimit = Number(config["CIVICOPS_INTAKE_LIMIT"], 60),
        DispatchLimit = Number(config["CIVICOPS_DISPATCH_LIMIT"], 120),
        ToolLimit = Number(config["CIVICOPS_TOOL_LIMIT"], 120),
        AuthLimit = Number(config["CIVICOPS_AUTH_LIMIT"], 20)
    };
    private static int Number(string? value, int fallback) => int.TryParse(value, out var n) && n is > 0 and <= 10000 ? n : fallback;
}

public static class DataPaths
{
    public static string Resolve(IConfiguration config, IWebHostEnvironment env) =>
        string.IsNullOrWhiteSpace(config["CIVICOPS_DATA_DIR"])
            ? Path.Combine(env.ContentRootPath, "Data") : Path.GetFullPath(config["CIVICOPS_DATA_DIR"]!);
}

public sealed record DispatchRequired;

public sealed record ElevenLabsToolRequired;
