using Chorus.Data.Redesign;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignSchemaMigrationTests
{
    [Fact]
    public async Task EmptyDatabase_MigratesTwiceAndEnforcesCrossGameLiveSlotAndAssetBounds()
    {
        var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Pooling = false };
        var databaseName = "chorus_schema_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            adminBuilder.Database = databaseName;
            await using var database = new NpgsqlConnection(adminBuilder.ConnectionString);
            await database.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(database);
            await RedesignSchemaMigrator.ApplyAsync(database);

            await using (var count = new NpgsqlCommand("""
                SELECT count(*) FROM information_schema.tables
                WHERE table_schema IN ('control','auth','live','player','match','game','input','settlement','infra')
                  AND table_type='BASE TABLE'
                """, database))
                Assert.Equal(32, Convert.ToInt32(await count.ExecuteScalarAsync()));

            await using (var versions = new NpgsqlCommand("SELECT count(*) FROM infra.schema_migration", database))
                Assert.Equal(10, Convert.ToInt32(await versions.ExecuteScalarAsync()));

            await using (var slot = new NpgsqlCommand("""
                INSERT INTO live.slot(platform,canonical_live_session_key,tenant_id)
                VALUES ('douyin','verified-live-1','tenant-a')
                """, database))
                await slot.ExecuteNonQueryAsync();
            await using (var duplicate = new NpgsqlCommand("""
                INSERT INTO live.slot(platform,canonical_live_session_key,tenant_id)
                VALUES ('douyin','verified-live-1','tenant-b')
                """, database))
                await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());

            await using var negative = new NpgsqlCommand("""
                INSERT INTO player.asset(tenant_id,game_id,player_id,asset_id,balance)
                VALUES ('tenant-a','game-a','player-a','coins',-1)
                """, database);
            await Assert.ThrowsAsync<PostgresException>(() => negative.ExecuteNonQueryAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
