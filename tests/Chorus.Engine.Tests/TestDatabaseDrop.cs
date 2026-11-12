using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Drops a test database, retrying the shared 42501 signature: PG18's
/// TerminateOtherDBBackends collects the database's backends under one lock and checks
/// each pid's privileges after releasing it, so a backend that exits in between can have
/// its OS pid reused by another role's connection under a co-tenant fork storm, and the
/// privilege check then lands on that stranger. The exiting backend is really gone within
/// milliseconds, so a short bounded retry settles it.
/// </summary>
internal static class TestDatabaseDrop
{
    public const int MaxAttempts = 5;

    public static async Task DropAsync(NpgsqlConnection admin, string databaseName)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var drop = new NpgsqlCommand(
                    $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
                return;
            }
            catch (PostgresException error) when (error.SqlState == "42501" && attempt < MaxAttempts)
            {
                await Task.Delay(100 * attempt);
            }
        }
    }
}
