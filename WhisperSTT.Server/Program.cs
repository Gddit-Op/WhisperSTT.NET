using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;
using WhisperSTT.Server.Configuration;
using WhisperSTT.Server.Services;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, RemoteTranscriptionServerJsonContext.Default);
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
builder.Services.AddSingleton<RemoteTranscriptionRequestHandler>();

var app = builder.Build();

app.MapGet(
    "/",
    () => Results.Ok(new ServerStatusResponse(
        "WhisperSTT.Server",
        RemoteTranscriptionProtocolConstants.TranscriptionEndpoint,
        paths.RootDirectory)));

app.MapPost(
    RemoteTranscriptionProtocolConstants.TranscriptionEndpoint,
    async (HttpRequest request, RemoteTranscriptionRequestHandler handler, IActivityLogService activityLogService, CancellationToken cancellationToken) =>
    {
        try
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Expected multipart/form-data.");
            }

            var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            var metadataJson = form["metadata"].ToString();
            if (string.IsNullOrWhiteSpace(metadataJson))
            {
                return Results.BadRequest("Missing metadata field.");
            }

            var metadata = JsonSerializer.Deserialize(
                metadataJson,
                RemoteTranscriptionServerJsonContext.Default.RemoteTranscriptionStartMessage);
            if (metadata is null || !metadata.IsValid)
            {
                return Results.BadRequest("Invalid metadata payload.");
            }

            var payloadFile = form.Files.GetFile("payload");
            if (payloadFile is null)
            {
                return Results.BadRequest("Missing payload file.");
            }

            await using var payloadStream = payloadFile.OpenReadStream();
            using var memoryStream = new MemoryStream();
            await payloadStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);

            var response = await handler
                .ProcessAsync(metadata, memoryStream.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (Exception exception)
        {
            await activityLogService
                .WriteAsync($"Remote transcription request failed: {exception.GetType().Name}: {exception}")
                .ConfigureAwait(false);
            return Results.Problem(
                title: "Remote transcription request failed",
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
