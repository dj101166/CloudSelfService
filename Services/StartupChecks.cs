using CloudSelfService.Services.Identity;

namespace CloudSelfService.Services;

/// <summary>Configuration checks Program runs once at startup, kept pure so the decisions are unit-tested.</summary>
public static class StartupChecks
{
    public const string ConnectionStringRequiredMessage = "ConnectionStrings:Default is required outside Development.";
    public const string NoOwnersWarning = "No Owners configured; nobody can assign staff roles.";

    /// <summary>Whether the app runs on PostgreSQL. Outside Development a missing connection string fails the start rather than silently losing every record on restart.</summary>
    public static bool UsePostgres(bool isDevelopment, string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString)) return true;
        if (!isDevelopment) throw new InvalidOperationException(ConnectionStringRequiredMessage);
        return false;
    }

    /// <summary>The warning to log when no Owner is configured outside Development; null otherwise.</summary>
    public static string? OwnersWarning(bool isDevelopment, OwnerConfig owners) =>
        !isDevelopment && owners.Upns.Count == 0 ? NoOwnersWarning : null;
}
