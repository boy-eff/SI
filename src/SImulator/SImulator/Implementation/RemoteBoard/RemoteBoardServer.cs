using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using SImulator.Properties;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Utils;
using Utils.Web;

namespace SImulator.Implementation.RemoteBoard;

/// <summary>
/// Serves the game board (webtable) to an external browser and bridges board messages over WebSocket.
/// </summary>
/// <remarks>
/// Replaces the embedded WebView2 where it cannot be rendered (e.g. under Wine/CrossOver on macOS).
/// The page gets a <c>window.chrome.webview</c> shim backed by WebSocket, so the board scripts stay unchanged.
/// Security:
/// <list type="bullet">
/// <item>the server listens on the loopback interface only;</item>
/// <item>the WebSocket requires the random session token from the board URL and the board origin,
/// so other pages opened in the browser cannot control the game;</item>
/// <item>only one board client is allowed at a time;</item>
/// <item>only the board files, the sounds folder and the media files sent to the board by the game are served.</item>
/// </list>
/// </remarks>
internal sealed class RemoteBoardServer : IAsyncDisposable
{
    /// <summary>
    /// WebSocket close status sent to a second board client.
    /// </summary>
    private const int BoardAlreadyOpenedCloseStatus = 4001;

    private const string ShimTemplate = """
        <script>
        (function () {
            var texts = __TEXTS__;
            var token = new URLSearchParams(location.hash.substring(1)).get('token') || '';
            var listeners = [];
            var queue = [];
            var ws = new WebSocket('ws://' + location.host + '/ws?token=' + encodeURIComponent(token));
            function showMessage(text) {
                var overlay = document.createElement('div');
                overlay.textContent = text;
                overlay.style.cssText = 'position:fixed;inset:0;z-index:100000;display:flex;align-items:center;justify-content:center;' +
                    'padding:2em;text-align:center;font:2em sans-serif;color:#fff;background:rgba(0,0,40,.92)';
                document.body.appendChild(overlay);
            }
            ws.onopen = function () { while (queue.length) { ws.send(queue.shift()); } };
            // Package media (served from /media/) created by the board, for media control from SImulator
            var packageMedia = new Set();
            var originalPlay = HTMLMediaElement.prototype.play;
            HTMLMediaElement.prototype.play = function () {
                if ((this.currentSrc || this.src || '').indexOf('/media/') >= 0) { packageMedia.add(this); }
                return originalPlay.apply(this, arguments);
            };
            function forEachMedia(action) {
                document.querySelectorAll('video, audio').forEach(function (m) {
                    if ((m.currentSrc || m.src || '').indexOf('/media/') >= 0) { packageMedia.add(m); }
                });
                packageMedia.forEach(function (m) { try { action(m); } catch (e) { } });
            }
            function controlMedia(action) {
                if (action === 'pause') { forEachMedia(function (m) { m.pause(); }); }
                else if (action === 'resume') { forEachMedia(function (m) { if (m.isConnected || !m.ended) { m.play(); } }); }
                else if (action === 'restart') { forEachMedia(function (m) { if (m.isConnected) { m.currentTime = 0; m.play(); } }); }
            }
            ws.onmessage = function (e) {
                var data = JSON.parse(e.data);
                if (data && data.type === '__siMediaControl') { controlMedia(data.action); return; }
                listeners.slice().forEach(function (l) { l({ data: data }); });
            };
            ws.onclose = function (e) { showMessage(e.code === __BUSY__ ? texts.alreadyOpened : texts.disconnected); };
            window.chrome = window.chrome || {};
            window.chrome.webview = {
                postMessage: function (msg) {
                    var text = JSON.stringify(msg, function (k, v) { return v instanceof Error ? String(v) : v; });
                    if (ws.readyState === 1) { ws.send(text); } else if (ws.readyState === 0) { queue.push(text); }
                },
                addEventListener: function (type, l) { if (type === 'message') { listeners.push(l); } },
                removeEventListener: function (type, l) { listeners = listeners.filter(function (x) { return x !== l; }); }
            };
        })();
        </script>
        """;

    private readonly WebApplication _app;
    private readonly IWebInterop _interop;
    private readonly byte[] _token;
    private readonly string _origin;
    private readonly object _clientLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _mediaIdsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _mediaPathsById = new();
    private readonly ConcurrentDictionary<string, byte> _videoIds = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _preparedVideos = new();
    private readonly string _mediaTempDirectory = Path.Combine(Path.GetTempPath(), $"simulator-media-{Guid.NewGuid():N}");
    private WebSocket? _client;

    /// <summary>
    /// Board URL. Contains the session token in the fragment, so it is not sent in HTTP requests.
    /// </summary>
    public string Url { get; }

    private RemoteBoardServer(WebApplication app, IWebInterop interop, int port, string token)
    {
        _app = app;
        _interop = interop;
        _token = Encoding.ASCII.GetBytes(token);
        _origin = $"http://127.0.0.1:{port}";
        Url = $"{_origin}/webtable/index.html#token={token}";

        _interop.SendJsonMessage += OnSendJsonMessage;
    }

    public static async Task<RemoteBoardServer> StartAsync(IWebInterop interop, int port)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = baseDir });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}/");

        var app = builder.Build();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var server = new RemoteBoardServer(app, interop, port, token);

        var shim = ShimTemplate
            .Replace("__TEXTS__", JsonSerializer.Serialize(new
            {
                alreadyOpened = Resources.BrowserBoardAlreadyOpened,
                disconnected = Resources.BrowserBoardDisconnected,
            }))
            .Replace("__BUSY__", BoardAlreadyOpenedCloseStatus.ToString());

        app.UseWebSockets();

        app.MapGet("/webtable/index.html", async () =>
        {
            var html = await File.ReadAllTextAsync(Path.Combine(baseDir, "webtable", "index.html"));
            return Results.Content(html.Replace("<head>", "<head>" + shim), "text/html; charset=utf-8");
        });

        var contentTypes = new FileExtensionContentTypeProvider();

        app.MapGet("/media/{id}", async (HttpContext context, string id) =>
        {
            if (!server._mediaPathsById.TryGetValue(id, out var path) || !File.Exists(path))
            {
                return Results.NotFound();
            }

            if (server._videoIds.ContainsKey(id))
            {
                var originalPath = path;
                path = await server.GetBrowserCompatibleVideoAsync(id, path);
                context.Response.Headers["X-SImulator-Video"] = path == originalPath ? "original" : "transcoded";
            }

            if (!contentTypes.TryGetContentType(path, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return Results.File(path, contentType, enableRangeProcessing: true);
        });

        app.Map("/ws", server.HandleWebSocketAsync);

        foreach (var folder in new[] { "webtable", "sounds" })
        {
            var folderPath = Path.Combine(baseDir, folder);

            if (Directory.Exists(folderPath))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(folderPath),
                    RequestPath = "/" + folder,
                    ServeUnknownFileTypes = true,
                });
            }
        }

        await app.StartAsync();

        return server;
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var token = Encoding.ASCII.GetBytes(context.Request.Query["token"].ToString());

        if (context.Request.Headers.Origin != _origin || !CryptographicOperations.FixedTimeEquals(token, _token))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        lock (_clientLock)
        {
            if (_client == null)
            {
                _client = socket;
            }
        }

        if (_client != socket)
        {
            await socket.CloseAsync((WebSocketCloseStatus)BoardAlreadyOpenedCloseStatus, "Board is already opened", context.RequestAborted);
            return;
        }

        try
        {
            await ReceiveMessagesAsync(socket, context.RequestAborted);
        }
        finally
        {
            lock (_clientLock)
            {
                _client = null;
            }
        }
    }

    private async Task ReceiveMessagesAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();

            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);

                // WebView2 raises WebMessageReceived on the UI thread; keep the same contract
                await UI.ExecuteAsync(
                    () =>
                    {
                        _interop.OnMessage(text);
                        return true;
                    },
                    exc => Trace.TraceError(exc.ToString()));
            }
        }
        catch (Exception exc) when (exc is WebSocketException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Controls package media (video and audio) on the board.
    /// </summary>
    /// <param name="action">pause, resume or restart.</param>
    public void ControlMedia(string action)
    {
        var client = _client;

        if (client != null)
        {
            _ = SendAsync(client, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "__siMediaControl", action })));
        }
    }

    private void OnSendJsonMessage(string json)
    {
        var client = _client;

        if (client == null)
        {
            return;
        }

        _ = SendAsync(client, Encoding.UTF8.GetBytes(RewriteLocalPaths(json)));
    }

    private async Task SendAsync(WebSocket socket, byte[] payload)
    {
        await _sendLock.WaitAsync();

        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Replaces local file paths and file:// URIs with media URLs served by this server, so the external browser can load package media.
    /// </summary>
    /// <remarks>
    /// Only the files referenced in the board messages become available to the browser.
    /// </remarks>
    private string RewriteLocalPaths(string json)
    {
        var node = JsonNode.Parse(json);

        if (node == null)
        {
            return json;
        }

        Rewrite(node);
        return node.ToJsonString();
    }

    private void Rewrite(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    var isVideo = key.Equals("value", StringComparison.OrdinalIgnoreCase)
                        && IsVideoContent(obj);

                    if (obj[key] is { } child && TryRewriteValue(child, isVideo) is { } replacement)
                    {
                        obj[key] = replacement;
                    }
                    else if (obj[key] is { } nested)
                    {
                        Rewrite(nested);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child && TryRewriteValue(child, false) is { } replacement)
                    {
                        array[i] = replacement;
                    }
                    else if (array[i] is { } nested)
                    {
                        Rewrite(nested);
                    }
                }

                break;
        }
    }

    private static bool IsVideoContent(JsonObject obj)
    {
        var type = obj.FirstOrDefault(property => property.Key.Equals("type", StringComparison.OrdinalIgnoreCase)).Value;

        return type is JsonValue value
            && ((value.TryGetValue<int>(out var numericType) && numericType == 3)
                || (value.TryGetValue<string>(out var stringType) && stringType.Equals("video", StringComparison.OrdinalIgnoreCase)));
    }

    private JsonNode? TryRewriteValue(JsonNode node, bool isVideo)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || !TryGetLocalPath(text, out var path))
        {
            return null;
        }

        var id = _mediaIdsByPath.GetOrAdd(path, _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        _mediaPathsById[id] = path;

        if (isVideo || IsLikelyVideoPath(path))
        {
            _videoIds[id] = 0;
        }

        return JsonValue.Create($"/media/{id}");
    }

    private static bool IsLikelyVideoPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".avi" or ".m4v" or ".mkv" or ".mov" or ".mp4" or ".mpeg" or ".mpg" or ".ogv" or ".webm" or ".wmv" => true,
        _ => false,
    };

    private Task<string> GetBrowserCompatibleVideoAsync(string id, string sourcePath) =>
        _preparedVideos.GetOrAdd(
            id,
            _ => new Lazy<Task<string>>(
                () => PrepareVideoAsync(id, sourcePath),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private async Task<string> PrepareVideoAsync(string id, string sourcePath)
    {
        string? temporaryPath = null;

        try
        {
            Directory.CreateDirectory(_mediaTempDirectory);
            Trace.TraceInformation($"Preparing browser video '{sourcePath}' in '{_mediaTempDirectory}'");

            if (await IsBrowserCompatibleAsync(sourcePath))
            {
                Trace.TraceInformation($"Video '{sourcePath}' is already browser-compatible");
                return sourcePath;
            }

            var outputPath = Path.Combine(_mediaTempDirectory, $"{id}.mp4");
            temporaryPath = outputPath + ".tmp";
            var startInfo = CreateToolStartInfo("ffmpeg");
            foreach (var argument in new[]
            {
                "-y", "-i", sourcePath, "-map", "0:v:0", "-map", "0:a?",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                "-pix_fmt", "yuv420p", "-movflags", "+faststart",
                "-c:a", "aac", "-b:a", "160k", "-f", "mp4", temporaryPath,
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffmpeg");
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var standardError = await standardErrorTask;

            if (process.ExitCode != 0 || !File.Exists(temporaryPath))
            {
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {standardError}");
            }

            File.Move(temporaryPath, outputPath, true);
            temporaryPath = null;
            Trace.TraceInformation($"Video '{sourcePath}' was transcoded to '{outputPath}'");
            return outputPath;
        }
        catch (Exception exc)
        {
            Trace.TraceError($"Could not prepare video '{sourcePath}': {exc}");
            return sourcePath;
        }
        finally
        {
            if (temporaryPath != null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static async Task<bool> IsBrowserCompatibleAsync(string sourcePath)
    {
        var startInfo = CreateToolStartInfo("ffprobe");
        foreach (var argument in new[] { "-v", "error", "-show_streams", "-of", "json", sourcePath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffprobe");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return false;
        }

        using var document = JsonDocument.Parse(output);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(stream => stream.GetProperty("codec_type").GetString() == "video");
        var audio = streams.FirstOrDefault(stream => stream.GetProperty("codec_type").GetString() == "audio");

        return video.ValueKind != JsonValueKind.Undefined
            && video.GetProperty("codec_name").GetString() == "h264"
            && video.TryGetProperty("pix_fmt", out var pixelFormat)
            && pixelFormat.GetString() == "yuv420p"
            && (audio.ValueKind == JsonValueKind.Undefined || audio.GetProperty("codec_name").GetString() == "aac");
    }

    private static ProcessStartInfo CreateToolStartInfo(string toolName)
    {
        var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, toolName);
        var toolPath = File.Exists(bundledPath) ? bundledPath : toolName;
        Trace.TraceInformation($"Using {toolName}: {toolPath}");

        return new ProcessStartInfo(toolPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    private static bool TryGetLocalPath(string text, out string path)
    {
        path = "";

        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc)
            {
                return false;
            }

            text = uri.LocalPath;
        }

        // Local paths only: drive paths on Windows (C:\... or C:/...), absolute paths of existing files on Unix;
        // network (UNC) paths are not allowed
        var isWindowsPath = text.Length > 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/');
        var isUnixPath = !OperatingSystem.IsWindows() && text.Length > 1 && text[0] == '/' && text[1] != '/' && File.Exists(text);

        if (!isWindowsPath && !isUnixPath)
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(text);
            return true;
        }
        catch (Exception exc) when (exc is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _interop.SendJsonMessage -= OnSendJsonMessage;
        _client?.Abort();

        await _app.StopAsync();
        await _app.DisposeAsync();

        var pendingConversions = _preparedVideos.Values
            .Where(preparedVideo => preparedVideo.IsValueCreated)
            .Select(preparedVideo => preparedVideo.Value)
            .ToArray();

        if (pendingConversions.Length > 0)
        {
            await Task.WhenAll(pendingConversions);
        }

        try
        {
            Directory.Delete(_mediaTempDirectory, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException exc)
        {
            Trace.TraceError($"Could not remove temporary media directory '{_mediaTempDirectory}': {exc}");
        }
        catch (UnauthorizedAccessException exc)
        {
            Trace.TraceError($"Could not remove temporary media directory '{_mediaTempDirectory}': {exc}");
        }
    }
}
