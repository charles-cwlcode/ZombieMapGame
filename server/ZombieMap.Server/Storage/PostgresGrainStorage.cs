using Newtonsoft.Json;
using ZombieMap.Core.Protocol;
using Npgsql;
using NpgsqlTypes;
using Orleans.Storage;

namespace ZombieMap.Server.Storage;

/// <summary>
/// Grain state (players, their loot, encounters, cached floor plans) in the same PostgreSQL as the
/// map data. One row per grain and state name; the state is JSON (jsonb), so it can be read and
/// fixed with plain SQL. The ETag is a counter: a write only succeeds if nobody wrote in between.
/// </summary>
public sealed class PostgresGrainStorage(NpgsqlDataSource db, DatabaseReady ready) : IGrainStorage
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS grain_state (
            grain_type  text   NOT NULL,   -- e.g. "player"
            grain_key   text   NOT NULL,   -- e.g. the player ID
            state_name  text   NOT NULL,   -- the [PersistentState] name
            etag        bigint NOT NULL,
            data        jsonb  NOT NULL,
            updated_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (grain_type, grain_key, state_name)
        );
        """;

    private static JsonSerializerSettings Json => ProtocolJson.StorageSettings;

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await ready.WaitAsync();
        await using var cmd = db.CreateCommand(
            "SELECT etag, data::text FROM grain_state WHERE grain_type = @t AND grain_key = @k AND state_name = @n");
        AddKey(cmd, stateName, grainId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            grainState.State = JsonConvert.DeserializeObject<T>(reader.GetString(1), Json) ?? Activator.CreateInstance<T>();
            grainState.ETag = reader.GetInt64(0).ToString();
            grainState.RecordExists = true;
        }
        else
        {
            grainState.State = Activator.CreateInstance<T>();
            grainState.ETag = null;
            grainState.RecordExists = false;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await ready.WaitAsync();
        string json = JsonConvert.SerializeObject(grainState.State, Json);
        long next;
        int rows;
        if (grainState.ETag == null)
        {
            next = 1;
            await using var cmd = db.CreateCommand("""
                INSERT INTO grain_state (grain_type, grain_key, state_name, etag, data)
                VALUES (@t, @k, @n, 1, @d) ON CONFLICT DO NOTHING
                """);
            AddKey(cmd, stateName, grainId);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = json });
            rows = await cmd.ExecuteNonQueryAsync();
        }
        else
        {
            long current = long.Parse(grainState.ETag);
            next = current + 1;
            await using var cmd = db.CreateCommand("""
                UPDATE grain_state SET data = @d, etag = @next, updated_utc = now()
                WHERE grain_type = @t AND grain_key = @k AND state_name = @n AND etag = @etag
                """);
            AddKey(cmd, stateName, grainId);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = json });
            cmd.Parameters.AddWithValue("next", next);
            cmd.Parameters.AddWithValue("etag", current);
            rows = await cmd.ExecuteNonQueryAsync();
        }
        if (rows == 0)
            throw new InconsistentStateException($"{grainId} '{stateName}' was changed by someone else.", null, grainState.ETag);
        grainState.ETag = next.ToString();
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await ready.WaitAsync();
        await using var cmd = db.CreateCommand(
            "DELETE FROM grain_state WHERE grain_type = @t AND grain_key = @k AND state_name = @n");
        AddKey(cmd, stateName, grainId);
        await cmd.ExecuteNonQueryAsync();
        grainState.State = Activator.CreateInstance<T>();
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private static void AddKey(NpgsqlCommand cmd, string stateName, GrainId grainId)
    {
        cmd.Parameters.AddWithValue("t", grainId.Type.ToString()!);
        cmd.Parameters.AddWithValue("k", grainId.Key.ToString()!);
        cmd.Parameters.AddWithValue("n", stateName);
    }
}

/// <summary>
/// Set by <see cref="World.WorldStartup"/> once the database is up and its tables exist (before the
/// slow map import), so saving players doesn't wait for map data.
/// </summary>
public sealed class DatabaseReady
{
    /// <summary>
    /// Shorter than Orleans' 30 s call timeout, so a player connecting while Docker is still starting
    /// gets "still starting" rather than a timeout. Their next try succeeds once it's up.
    /// </summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void MarkReady() => _ready.TrySetResult();
    public void MarkFailed(Exception ex) => _ready.TrySetException(ex);

    public async Task WaitAsync()
    {
        try
        {
            await _ready.Task.WaitAsync(Wait);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The database is still starting. Try again in a moment.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The database is offline, so nothing can be saved: " + ex.Message, ex);
        }
    }
}
