using Microsoft.Extensions.Options;
using Npgsql;
using Orleans.Serialization;
using Orleans.Providers;
using Orleans.Runtime.Hosting;
using ZombieMap.Server.Gateway;
using ZombieMap.Server.Storage;
using ZombieMap.Server.World;

var builder = WebApplication.CreateBuilder(args);

// Your own map areas and other machine-specific settings (git-ignored).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

// Real-world map data: PostGIS, filled from Overture Maps. WorldStartup gets it ready in the
// background (starting Docker in Development) so the server is usable straight away.
builder.Services.Configure<WorldOptions>(builder.Configuration.GetSection(WorldOptions.Section));
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<WorldOptions>>().Value.ConnectionString));
builder.Services.AddSingleton<WorldStore>();
builder.Services.AddSingleton<OvertureImporter>();
builder.Services.AddHostedService<WorldStartup>();

// Players, loot, encounters and floor plans are saved in the same PostgreSQL (Storage:Provider
// "Postgres", the default). "Memory" forgets everything on restart: only for quick experiments.
builder.Services.AddSingleton<DatabaseReady>();
bool memoryStorage = string.Equals(builder.Configuration["Storage:Provider"], "Memory", StringComparison.OrdinalIgnoreCase);

// Orleans silo hosted in the same process as the WebSocket gateway. One local silo for now.
builder.UseOrleans(silo =>
{
    // Ports are configurable so a second copy can run next to one in the debugger:
    //   dotnet run --project server/ZombieMap.Server -- --urls http://localhost:5081 --Orleans:SiloPort 11112 --Orleans:GatewayPort 30001
    silo.UseLocalhostClustering(
        siloPort: builder.Configuration.GetValue("Orleans:SiloPort", 11111),
        gatewayPort: builder.Configuration.GetValue("Orleans:GatewayPort", 30000));
    if (memoryStorage) silo.AddMemoryGrainStorageAsDefault();
    else silo.Services.AddGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
        (sp, _) => ActivatorUtilities.CreateInstance<PostgresGrainStorage>(sp));
    // Shared ZombieMap.Core types have no Orleans attributes (Unity compiles them too),
    // so they cross grain calls and storage as JSON.
    silo.Services.AddSerializer(serializer => serializer.AddNewtonsoftJsonSerializer(
        isSupported: type => type.Namespace?.StartsWith("ZombieMap.Core", StringComparison.Ordinal) == true));
});

builder.Services.AddSingleton<GameSocketHandler>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/", (WorldStore world) => $"ZombieMap server is running. Connect a WebSocket to /ws. Map data: {world.Status}.");

app.Map("/ws", async (HttpContext context, GameSocketHandler handler) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        await handler.RunAsync(socket, context.RequestAborted);
    }
    catch (OperationCanceledException) { }
    catch (System.Net.WebSockets.WebSocketException) { } // client dropped
});

app.Run();
