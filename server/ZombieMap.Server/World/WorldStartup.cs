using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using ZombieMap.Server.Storage;

namespace ZombieMap.Server.World;

/// <summary>
/// Gets the map database ready in the background while the server is already accepting connections:
///   1. Development: start Docker Desktop if needed, then `docker compose up` the PostGIS container.
///   2. Create tables. From here players can be saved (<see cref="DatabaseReady"/>).
///   3. Import any configured area that isn't in the database yet (Overture → PostGIS, ~1 min a town).
/// If anything fails, map features report "world_offline" and everything else keeps working.
/// </summary>
public sealed class WorldStartup(
    WorldStore store,
    NpgsqlDataSource db,
    OvertureImporter importer,
    DatabaseReady databaseReady,
    IOptions<WorldOptions> options,
    IHostEnvironment env,
    ILogger<WorldStartup> log) : BackgroundService
{
    private const string DockerDesktopPath = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
    private readonly WorldOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            if (env.IsDevelopment() && _options.AutoStartDatabase)
            {
                store.MarkLoading("starting database");
                await StartDatabaseContainerAsync(ct);
            }

            store.MarkLoading("connecting to database");
            await WaitForDatabaseAsync(ct);

            // Players can be saved from here on, even while the map is still importing.
            await using (var cmd = db.CreateCommand(PostgresGrainStorage.Schema))
                await cmd.ExecuteNonQueryAsync(ct);
            databaseReady.MarkReady();

            await using (var cmd = db.CreateCommand(WorldSchema.Sql))
                await cmd.ExecuteNonQueryAsync(ct);

            await ImportMissingAreasAsync(ct);

            await using (var cmd = db.CreateCommand("SELECT count(*) FROM buildings"))
                log.LogInformation("Map data ready: {Count} buildings", await cmd.ExecuteScalarAsync(ct));
            store.MarkReady();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogError(ex, "Map data is offline. Encounters still work; building lookups will fail. " +
                             "Is Docker Desktop installed and running?");
            store.MarkOffline(ex.Message);
            databaseReady.MarkFailed(ex); // no effect if the database itself came up
        }
    }

    private async Task ImportMissingAreasAsync(CancellationToken ct)
    {
        var areas = _options.Areas.Where(a => a.IsValid).ToList();
        foreach (var bad in _options.Areas.Where(a => !a.IsValid))
            log.LogWarning("Ignoring map area {Area}: needs a name, West<East, South<North and at most 0.5° a side", bad);
        if (areas.Count == 0)
        {
            log.LogWarning("No map areas configured, so there are no buildings. Add World:Areas to appsettings.Local.json " +
                           "(see appsettings.Local.example.json).");
            return;
        }

        string? release = null;
        foreach (var area in areas)
        {
            if (await IsImportedAsync(area, ct))
            {
                // Re-run the building-type rules every start (a few seconds), so rule changes
                // apply without downloading the area again.
                await importer.ClassifyAsync(area, ct);
                log.LogInformation("Map area {Area} already imported; building types refreshed", area.Name);
                continue;
            }
            store.MarkLoading($"importing {area.Name}");
            release ??= await importer.ResolveReleaseAsync(_options.OvertureRelease, ct);
            await importer.ImportAsync(area, release, ct);
        }
    }

    private async Task<bool> IsImportedAsync(MapArea area, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT 1 FROM world_areas WHERE name = @n AND west = @w AND south = @s AND east = @e AND north = @no");
        cmd.Parameters.AddWithValue("n", area.Name);
        cmd.Parameters.AddWithValue("w", area.West);
        cmd.Parameters.AddWithValue("s", area.South);
        cmd.Parameters.AddWithValue("e", area.East);
        cmd.Parameters.AddWithValue("no", area.North);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    // ---------- Docker (Development only) ----------

    private async Task StartDatabaseContainerAsync(CancellationToken ct)
    {
        string compose = FindComposeFile()
            ?? throw new InvalidOperationException("Couldn't find docker-compose.yml above " + env.ContentRootPath);

        if (!await DockerRunningAsync(ct))
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(DockerDesktopPath))
                throw new InvalidOperationException("Docker isn't running. Start it, or set World:AutoStartDatabase=false and run PostGIS yourself.");

            log.LogInformation("Starting Docker Desktop…");
            Process.Start(new ProcessStartInfo(DockerDesktopPath) { UseShellExecute = true });
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (!await DockerRunningAsync(ct))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Docker Desktop didn't start within 3 minutes.");
                await Task.Delay(2000, ct);
            }
        }

        log.LogInformation("Starting PostGIS container ({Compose})…", compose);
        var (code, output) = await RunAsync("docker", $"compose -f \"{compose}\" up -d --wait", TimeSpan.FromMinutes(10), ct);
        if (code != 0) throw new InvalidOperationException("docker compose up failed: " + output.Trim());
    }

    private async Task<bool> DockerRunningAsync(CancellationToken ct)
    {
        try
        {
            var (code, _) = await RunAsync("docker", "info --format {{.ServerVersion}}", TimeSpan.FromSeconds(15), ct);
            return code == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // docker CLI not installed
        }
    }

    private string? FindComposeFile()
    {
        for (var dir = new DirectoryInfo(env.ContentRootPath); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "docker-compose.yml");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<(int code, string output)> RunAsync(string file, string args, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (-1, $"{file} {args} timed out");
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}
