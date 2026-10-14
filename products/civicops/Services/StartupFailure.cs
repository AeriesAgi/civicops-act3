namespace CivicOps.Services;

/// <summary>Turns an unhandled startup/config exception into one clear stderr line, then the stack trace, then exit code 1.</summary>
public static class StartupFailure
{
    public static string Describe(Exception exception)
    {
        var root = exception;
        while (root is System.Reflection.TargetInvocationException or AggregateException && root.InnerException is not null) root = root.InnerException;
        var parts = new List<string>();
        for (var current = root; current is not null; current = current.InnerException)
            if (!parts.Contains(current.Message)) parts.Add(current.Message);
        var message = string.Join(" <- ", parts).ReplaceLineEndings(" ");
        return root switch
        {
            UnauthorizedAccessException => $"CIVICOPS STARTUP FAILED: {message} The container runs as uid 1654; make CIVICOPS_DATA_DIR (default /app/Data) writable by it (for Azure Files use mount options uid=1654,gid=1654) or point it at an unmounted path.",
            _ => $"CIVICOPS STARTUP FAILED: {root.GetType().Name}: {message}"
        };
    }

    public static void Register() => AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        var exception = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString());
        var line = Describe(exception);
        // Portals often show only the last lines, so the summary is printed before and after the stack trace.
        Console.Error.WriteLine(line);
        Console.Error.WriteLine(exception);
        Console.Error.WriteLine(line);
        Console.Error.Flush();
        Console.Out.WriteLine(line);
        Console.Out.Flush();
        Environment.Exit(1);
    };
}
