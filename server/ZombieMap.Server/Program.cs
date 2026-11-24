using Orleans.Serialization;
using ZombieMap.Server.Gateway;

var builder = WebApplication.CreateBuilder(args);

// Orleans silo hosted in the same process as the WebSocket gateway.
// Phase 0: one local silo, in-memory storage. Later: PostgreSQL storage + clustering.
builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorageAsDefault();
    // Shared ZombieMap.Core types have no Orleans attributes (Unity compiles them too),
    // so they cross grain calls and storage as JSON.
    silo.Services.AddSerializer(serializer => serializer.AddNewtonsoftJsonSerializer(
        isSupported: type => type.Namespace?.StartsWith("ZombieMap.Core", StringComparison.Ordinal) == true));
});

builder.Services.AddSingleton<GameSocketHandler>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/", () => "ZombieMap server is running. Connect a WebSocket to /ws.");

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
