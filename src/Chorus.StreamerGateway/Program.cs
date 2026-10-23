using Chorus.Gateway;
using Chorus.Gateway.Core.Faces;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGatewayRuntime();
builder.Services.AddSingleton(StreamerWsFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(sp => new IpcSessionRouter(
    sp.GetRequiredService<GatewayIpcService>(), sp.GetRequiredService<FaceCounters>()));
builder.Services.AddSingleton(sp => new StreamerWsFace(
    sp.GetRequiredService<StreamerWsFaceOptions>(), sp.GetRequiredService<IpcSessionRouter>(),
    sp.GetRequiredService<FaceCounters>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Chorus.StreamerGateway")));

var app = builder.Build();
app.UseWebSockets();
var options = app.Services.GetRequiredService<StreamerWsFaceOptions>();
if (options.AllowAnonymous)
    app.Logger.LogWarning("Anonymous streamer access is enabled. Development only.");
else if (options.Tokens.Count == 0)
    app.Logger.LogWarning("No streamer auth tokens configured; WebSocket authentication will reject connections.");
app.MapGatewayDiagnostics("Chorus.StreamerGateway");
app.Map("/ws", context => context.RequestServices.GetRequiredService<StreamerWsFace>().HandleAsync(context));
app.Run();
