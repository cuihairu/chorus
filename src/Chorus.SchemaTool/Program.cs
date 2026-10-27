using Chorus.Data.Redesign;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("CHORUS_DATABASE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set CHORUS_DATABASE_CONNECTION_STRING for the dedicated migration role.");
    return 2;
}

try
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await RedesignSchemaMigrator.ApplyAsync(connection);
    Console.WriteLine("Chorus redesign schema is current.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Schema migration failed: {error.GetType().Name}");
    return 1;
}
