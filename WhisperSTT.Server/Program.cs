using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;
using WhisperSTT.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var dataRoot = builder.Configuration["DataRoot"];
var paths = string.IsNullOrWhiteSpace(dataRoot)
    ? new ApplicationPaths()
    : new ApplicationPaths(dataRoot);
paths.EnsureCreated();

var settingsStore = new JsonSettingsStore(paths);
var settings = await settingsStore.LoadAsync().ConfigureAwait(false);
var logger = new FileActivityLogService(paths);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<IActivityLogService>(logger);
builder.Services.AddSingleton<ISettingsStore>(settingsStore);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<WhisperModelService>();
builder.Services.AddSingleton<WhisperServerTranscriptionService>();
builder.Services.AddSingleton<WebRtcSessionRegistry>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "WhisperSTT.Server",
    signalingEndpoint = WebRtcProtocolConstants.SessionEndpoint,
    dataRoot = paths.RootDirectory
}));

app.MapPost(
    WebRtcProtocolConstants.SessionEndpoint,
    async (WebRtcOfferRequest request, WebRtcSessionRegistry registry, CancellationToken cancellationToken) =>
    {
        var response = await registry.CreateSessionAsync(request, cancellationToken).ConfigureAwait(false);
        return Results.Ok(response);
    });

app.Run();
