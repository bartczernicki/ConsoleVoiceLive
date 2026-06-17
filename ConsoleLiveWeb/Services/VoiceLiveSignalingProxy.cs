using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ConsoleLiveWeb.Services;

public sealed class VoiceLiveSignalingProxy
{
    private const string ApiVersion = "2026-01-01-preview";
    private const string DefaultVoiceName = "en-US-Ava:DragonHDLatestNeural";
    private const string DefaultModel = "gpt-realtime-1.5";
    private const string DefaultInstructions = "You are a helpful AI assistant. Respond naturally and conversationally. Keep your responses concise but engaging.";
    private const string PlaceholderEndpoint = "https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com";
    private const string PlaceholderKey = "YOUR_SPEECH_KEY";
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly IConfiguration _configuration;
    private readonly ILogger<VoiceLiveSignalingProxy> _logger;

    public VoiceLiveSignalingProxy(IConfiguration configuration, ILogger<VoiceLiveSignalingProxy> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Voice Live signaling requires a WebSocket request.");
            return;
        }

        using WebSocket browserSocket = await context.WebSockets.AcceptWebSocketAsync();

        VoiceLiveWebRtcSettings settings;
        try
        {
            settings = LoadSettings();
        }
        catch (InvalidOperationException ex)
        {
            await SendErrorAsync(browserSocket, ex.Message, context.RequestAborted);
            await CloseSocketAsync(browserSocket, WebSocketCloseStatus.PolicyViolation, "Configuration error");
            return;
        }

        using var azureSocket = new ClientWebSocket();
        azureSocket.Options.SetRequestHeader("api-key", settings.Key);

        try
        {
            await azureSocket.ConnectAsync(settings.ServiceUri, context.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to connect to Azure Voice Live WebRTC endpoint.");
            await SendErrorAsync(
                browserSocket,
                $"Unable to connect to Azure Voice Live. Confirm Speech:Endpoint, Speech:Key, and Speech:Model are valid for Voice Live WebRTC. {ex.Message}",
                CancellationToken.None);
            await CloseSocketAsync(browserSocket, WebSocketCloseStatus.PolicyViolation, "Azure connection failed");
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Task browserToAzure = ProxyBrowserToAzureAsync(browserSocket, azureSocket, linkedCts.Token);
        Task azureToBrowser = ProxyAzureToBrowserAsync(azureSocket, browserSocket, settings, linkedCts.Token);

        Task completed = await Task.WhenAny(browserToAzure, azureToBrowser);
        linkedCts.Cancel();

        try
        {
            await completed;
        }
        catch (OperationCanceledException)
        {
            // Expected when either side closes the conversation.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Voice Live signaling socket closed unexpectedly.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Voice Live signaling failed.");
            await SendErrorAsync(browserSocket, ex.Message, CancellationToken.None);
        }
        finally
        {
            await CloseSocketAsync(browserSocket, WebSocketCloseStatus.NormalClosure, "Voice Live signaling stopped");
            await CloseSocketAsync(azureSocket, WebSocketCloseStatus.NormalClosure, "Voice Live signaling stopped");
        }
    }

    private async Task ProxyBrowserToAzureAsync(WebSocket browserSocket, ClientWebSocket azureSocket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               browserSocket.State == WebSocketState.Open &&
               azureSocket.State == WebSocketState.Open)
        {
            WebSocketMessage? message = await ReceiveMessageAsync(browserSocket, cancellationToken);
            if (message is null)
            {
                break;
            }

            await SendMessageAsync(azureSocket, message, cancellationToken);
        }
    }

    private async Task ProxyAzureToBrowserAsync(
        ClientWebSocket azureSocket,
        WebSocket browserSocket,
        VoiceLiveWebRtcSettings settings,
        CancellationToken cancellationToken)
    {
        var sessionConfigured = false;

        while (!cancellationToken.IsCancellationRequested &&
               azureSocket.State == WebSocketState.Open &&
               browserSocket.State == WebSocketState.Open)
        {
            WebSocketMessage? message = await ReceiveMessageAsync(azureSocket, cancellationToken);
            if (message is null)
            {
                break;
            }

            await SendMessageAsync(browserSocket, message, cancellationToken);

            if (!sessionConfigured && ShouldConfigureSession(message))
            {
                string sessionUpdate = BuildSessionUpdate(settings);
                await SendTextAsync(azureSocket, sessionUpdate, cancellationToken);
                sessionConfigured = true;
            }
        }
    }

    private VoiceLiveWebRtcSettings LoadSettings()
    {
        string? endpoint = _configuration["Speech:Endpoint"];
        string? key = _configuration["Speech:Key"];
        string? voiceName = _configuration["Speech:VoiceName"];
        string? model = _configuration["Speech:Model"];
        string? instructions = _configuration["Speech:Instructions"];

        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == PlaceholderEndpoint)
        {
            throw new InvalidOperationException(
                "Set Speech:Endpoint in secrets.appsettings.json to your Azure Voice Live endpoint.");
        }

        if (string.IsNullOrWhiteSpace(key) || key == PlaceholderKey)
        {
            throw new InvalidOperationException(
                "Set Speech:Key in secrets.appsettings.json to your Azure Speech resource key.");
        }

        string selectedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        Uri serviceUri = BuildServiceUri(endpoint, selectedModel);

        return new VoiceLiveWebRtcSettings(
            endpoint,
            key,
            string.IsNullOrWhiteSpace(voiceName) ? DefaultVoiceName : voiceName,
            selectedModel,
            string.IsNullOrWhiteSpace(instructions) ? DefaultInstructions : instructions,
            serviceUri);
    }

    private static Uri BuildServiceUri(string endpoint, string model)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
        {
            throw new InvalidOperationException("Speech:Endpoint must be an absolute Azure endpoint URI.");
        }

        var builder = new UriBuilder(endpointUri)
        {
            Scheme = Uri.UriSchemeWss,
            Port = -1,
            Path = "voice-live/realtime/calls",
            Query = $"api-version={ApiVersion}&model={Uri.EscapeDataString(model)}"
        };

        return builder.Uri;
    }

    private static bool ShouldConfigureSession(WebSocketMessage message)
    {
        if (message.MessageType != WebSocketMessageType.Text)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(message.Payload);
            if (!document.RootElement.TryGetProperty("type", out JsonElement typeElement))
            {
                return false;
            }

            string? type = typeElement.GetString();
            return type is "rtc.call.sdp.created" or "session.created";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildSessionUpdate(VoiceLiveWebRtcSettings settings)
    {
        var session = new Dictionary<string, object?>
        {
            ["modalities"] = new[] { "text", "audio" },
            ["instructions"] = settings.Instructions,
            ["voice"] = new Dictionary<string, object?>
            {
                ["name"] = settings.VoiceName,
                ["type"] = "azure-standard"
            },
            ["input_audio_sampling_rate"] = 24000,
            ["input_audio_echo_cancellation"] = new Dictionary<string, object?>
            {
                ["type"] = "server_echo_cancellation"
            },
            ["input_audio_noise_reduction"] = new Dictionary<string, object?>
            {
                ["type"] = "azure_deep_noise_suppression"
            },
            ["turn_detection"] = new Dictionary<string, object?>
            {
                ["type"] = "server_vad",
                ["threshold"] = 0.5,
                ["prefix_padding_ms"] = 400,
                ["silence_duration_ms"] = 500
            }
        };

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = session
        });
    }

    private static async Task<WebSocketMessage?> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                stream.Write(buffer, 0, result.Count);

                if (stream.Length > MaxMessageBytes)
                {
                    throw new InvalidOperationException("Voice Live signaling message was too large.");
                }
            }
            while (!result.EndOfMessage);

            return new WebSocketMessage(result.MessageType, stream.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task SendMessageAsync(WebSocket socket, WebSocketMessage message, CancellationToken cancellationToken)
    {
        return socket.SendAsync(
            new ArraySegment<byte>(message.Payload),
            message.MessageType,
            endOfMessage: true,
            cancellationToken);
    }

    private static Task SendTextAsync(WebSocket socket, string message, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);

        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static Task SendErrorAsync(WebSocket socket, string message, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["error"] = new Dictionary<string, object?>
            {
                ["message"] = message
            }
        });

        return SendTextAsync(socket, payload, cancellationToken);
    }

    private static async Task CloseSocketAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(status, description, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // The peer already went away.
            }
        }
    }

    private sealed record VoiceLiveWebRtcSettings(
        string Endpoint,
        string Key,
        string VoiceName,
        string Model,
        string Instructions,
        Uri ServiceUri);

    private sealed record WebSocketMessage(WebSocketMessageType MessageType, byte[] Payload);
}
