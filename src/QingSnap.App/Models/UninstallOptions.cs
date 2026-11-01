namespace QingSnap.App.Models;

public sealed record UninstallOptions(
    bool DeleteHistory,
    bool DeleteDatabase,
    bool DeleteSettings,
    bool DeleteOcr,
    bool DeleteLogs)
{
    public static UninstallOptions KeepAll { get; } = new(false, false, false, false, false);
}
