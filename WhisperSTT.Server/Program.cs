using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Services;
using WhisperSTT.Server.Configuration;
using WhisperSTT.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, WebRtcServerJsonContext.Default);
});

var configuredOptions = builder.Configuration
    .GetSection(ServerOptions.SectionName)
    .Get<ServerOptions>() ?? new ServerOptions();
var whisperOptions = builder.Configuration
    .GetSection(WhisperServerTranscriptionOptions.SectionName)
    .Get<WhisperServerTranscriptionOptions>() ?? new WhisperServerTranscriptionOptions();
var dataRoot = ResolveConfiguredPath(configuredOptions.DataRoot, builder.Environment.ContentRootPath);
var logFilePath = ResolveConfiguredPath(configuredOptions.LogFilePath, builder.Environment.ContentRootPath);
whisperOptions.CustomModelPath = ResolveConfiguredPath(whisperOptions.CustomModelPath, builder.Environment.ContentRootPath);
whisperOptions.OpenVinoRuntimePath = ResolveConfiguredPath(whisperOptions.OpenVinoRuntimePath, builder.Environment.ContentRootPath);
var paths = new ApplicationPaths(
    rootDirectory: dataRoot,
    logPath: logFilePath);
paths.EnsureCreated();

var logger = new FileActivityLogService(paths);
await logger.WriteAsync(
    $"Server startup. DataRoot={paths.RootDirectory}; LogPath={paths.LogPath}; PreferServerWhisperConfiguration={configuredOptions.PreferServerWhisperConfiguration}; CustomModelPath={whisperOptions.CustomModelPath}; Runtime={whisperOptions.RuntimePreference}.")
    .ConfigureAwait(false);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<IActivityLogService>(logger);
builder.Services.AddSingleton(configuredOptions);
builder.Services.AddSingleton(whisperOptions);
builder.Services.AddSingleton<WhisperModelService>();
builder.Services.AddSingleton<WhisperServerTranscriptionService>();
builder.Services.AddSingleton<WebRtcSessionRegistry>();

var app = builder.Build();

app.MapGet(
    "/",
    () => Results.Ok(new ServerStatusResponse(
        "WhisperSTT.Server",
        WebRtcProtocolConstants.SessionEndpoint,
        paths.RootDirectory)));

app.MapPost(
    WebRtcProtocolConstants.SessionEndpoint,
    async (WebRtcOfferRequest request, WebRtcSessionRegistry registry, IActivityLogService activityLogService, CancellationToken cancellationToken) =>
    {
        try
        {
            var response = await registry.CreateSessionAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (Exception exception)
        {
            await activityLogService
                .WriteAsync($"WebRTC session creation failed: {exception.GetType().Name}: {exception}")
                .ConfigureAwait(false);
            return Results.Problem(
                title: "WebRTC session creation failed",
                detail: exception.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    });

app.Run();

static string ResolveConfiguredPath(string configuredPath, string contentRootPath)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
    {
        return string.Empty;
    }

    var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
    if (Path.IsPathRooted(expandedPath))
    {
        return expandedPath;
    }

    return Path.GetFullPath(Path.Combine(contentRootPath, expandedPath));
}
