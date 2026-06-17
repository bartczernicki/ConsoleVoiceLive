using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConsoleLiveWeb.Services;

public sealed class VoiceLiveAvatarProxy
{
    private const string ApiVersion = "2026-04-10";
    private const string DefaultVoiceName = "en-US-Ava:DragonHDLatestNeural";
    private const string DefaultModel = "gpt-realtime-1.5";
    private const string DefaultInstructions = "You are a helpful AI assistant. Respond naturally and conversationally. Keep your responses concise but engaging.";
    private const string DefaultAvatarCharacter = "lisa";
    private const string DefaultAvatarStyle = "casual-sitting";
    private const string DefaultAvatarBackgroundColor = "#FFFFFFFF";
    private const string PlaceholderEndpoint = "https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com";
    private const string PlaceholderKey = "YOUR_SPEECH_KEY";
    private const int MaxMessageBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConfiguration _configuration;
    private readonly ILogger<VoiceLiveAvatarProxy> _logger;

    public VoiceLiveAvatarProxy(IConfiguration configuration, ILogger<VoiceLiveAvatarProxy> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Voice Live Avatar requires a WebSocket request.");
            return;
        }

        using WebSocket browserSocket = await context.WebSockets.AcceptWebSocketAsync();

        VoiceLiveAvatarSettings settings;
        try
        {
            settings = LoadSettings();
        }
        catch (InvalidOperationException ex)
        {
            await SendProxyEventAsync(browserSocket, "proxy.error", ex.Message, context.RequestAborted);
            await CloseSocketAsync(browserSocket, WebSocketCloseStatus.PolicyViolation, "Configuration error");
            return;
        }

        using var azureSocket = new ClientWebSocket();
        azureSocket.Options.SetRequestHeader("api-key", settings.Key);

        try
        {
            await azureSocket.ConnectAsync(settings.ServiceUri, context.RequestAborted);
            await SendProxyEventAsync(browserSocket, "proxy.connected", "Connected to Azure Voice Live.", context.RequestAborted);
            await SendTextAsync(azureSocket, BuildSessionUpdate(settings), context.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to connect to Azure Voice Live Avatar endpoint.");
            await SendProxyEventAsync(
                browserSocket,
                "proxy.error",
                $"Unable to connect to Azure Voice Live Avatar. Confirm Speech:Endpoint, Speech:Key, Speech:Model, and avatar region support. {ex.Message}",
                CancellationToken.None);
            await CloseSocketAsync(browserSocket, WebSocketCloseStatus.PolicyViolation, "Azure connection failed");
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Task browserToAzure = ProxyBrowserToAzureAsync(browserSocket, azureSocket, linkedCts.Token);
        Task azureToBrowser = ProxyAzureToBrowserAsync(azureSocket, browserSocket, linkedCts.Token);

        Task completed = await Task.WhenAny(browserToAzure, azureToBrowser);
        linkedCts.Cancel();

        try
        {
            await completed;
        }
        catch (OperationCanceledException)
        {
            // Expected when either side closes.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Voice Live Avatar socket closed unexpectedly.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Voice Live Avatar proxy failed.");
            await SendProxyEventAsync(browserSocket, "proxy.error", ex.Message, CancellationToken.None);
        }
        finally
        {
            await CloseSocketAsync(browserSocket, WebSocketCloseStatus.NormalClosure, "Voice Live Avatar stopped");
            await CloseSocketAsync(azureSocket, WebSocketCloseStatus.NormalClosure, "Voice Live Avatar stopped");
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

            if (message.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            await SendMessageAsync(azureSocket, message, cancellationToken);
        }
    }

    private async Task ProxyAzureToBrowserAsync(ClientWebSocket azureSocket, WebSocket browserSocket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               azureSocket.State == WebSocketState.Open &&
               browserSocket.State == WebSocketState.Open)
        {
            WebSocketMessage? message = await ReceiveMessageAsync(azureSocket, cancellationToken);
            if (message is null)
            {
                break;
            }

            if (message.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            string sanitizedJson = SanitizeServerEvent(message.Payload);
            await SendTextAsync(browserSocket, sanitizedJson, cancellationToken);
        }
    }

    private VoiceLiveAvatarSettings LoadSettings()
    {
        string? endpoint = _configuration["Speech:Endpoint"];
        string? key = _configuration["Speech:Key"];
        string? voiceName = _configuration["Speech:VoiceName"];
        string? model = _configuration["Speech:Model"];
        string? instructions = _configuration["Speech:Instructions"];
        string? avatarCharacter = _configuration["Speech:AvatarCharacter"];
        string? avatarStyle = _configuration["Speech:AvatarStyle"];
        string? avatarBackgroundColor = _configuration["Speech:AvatarBackgroundColor"];

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

        return new VoiceLiveAvatarSettings(
            endpoint,
            key,
            string.IsNullOrWhiteSpace(voiceName) ? DefaultVoiceName : voiceName,
            selectedModel,
            string.IsNullOrWhiteSpace(instructions) ? DefaultInstructions : instructions,
            string.IsNullOrWhiteSpace(avatarCharacter) ? DefaultAvatarCharacter : avatarCharacter,
            string.IsNullOrWhiteSpace(avatarStyle) ? DefaultAvatarStyle : avatarStyle,
            string.IsNullOrWhiteSpace(avatarBackgroundColor) ? DefaultAvatarBackgroundColor : avatarBackgroundColor,
            BuildServiceUri(endpoint, selectedModel));
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
            Path = "voice-live/realtime",
            Query = $"api-version={ApiVersion}&model={Uri.EscapeDataString(model)}"
        };

        return builder.Uri;
    }

    private static string BuildSessionUpdate(VoiceLiveAvatarSettings settings)
    {
        var session = new Dictionary<string, object?>
        {
            ["modalities"] = new[] { "text", "audio" },
            ["instructions"] = settings.Instructions,
            ["voice"] = new Dictionary<string, object?>
            {
                ["name"] = settings.VoiceName,
                ["type"] = "azure-standard",
                ["temperature"] = 0.8
            },
            ["input_audio_format"] = "pcm16",
            ["output_audio_format"] = "pcm16",
            ["input_audio_sampling_rate"] = 24000,
            ["input_audio_transcription"] = new Dictionary<string, object?>
            {
                ["model"] = "azure-speech",
                ["language"] = "en"
            },
            ["input_audio_noise_reduction"] = new Dictionary<string, object?>
            {
                ["type"] = "azure_deep_noise_suppression"
            },
            ["input_audio_echo_cancellation"] = new Dictionary<string, object?>
            {
                ["type"] = "server_echo_cancellation"
            },
            ["turn_detection"] = new Dictionary<string, object?>
            {
                ["type"] = "azure_semantic_vad",
                ["threshold"] = 0.5,
                ["prefix_padding_ms"] = 420,
                ["silence_duration_ms"] = 500,
                ["interrupt_response"] = true,
                ["remove_filler_words"] = true
            },
            ["avatar"] = new Dictionary<string, object?>
            {
                ["character"] = settings.AvatarCharacter,
                ["style"] = settings.AvatarStyle,
                ["customized"] = false,
                ["video"] = new Dictionary<string, object?>
                {
                    ["bitrate"] = 2000000,
                    ["codec"] = "h264",
                    ["resolution"] = new Dictionary<string, object?>
                    {
                        ["width"] = 1920,
                        ["height"] = 1080
                    },
                    ["background"] = new Dictionary<string, object?>
                    {
                        ["color"] = settings.AvatarBackgroundColor
                    }
                }
            }
        };

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = session
        }, JsonOptions);
    }

    private static string SanitizeServerEvent(byte[] payload)
    {
        string json = Encoding.UTF8.GetString(payload);

        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is null)
            {
                return json;
            }

            string? eventType = node["type"]?.GetValue<string>();
            ScrubNode(node, eventType);

            return node.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void ScrubNode(JsonNode node, string? eventType)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (KeyValuePair<string, JsonNode?> property in jsonObject.ToArray())
            {
                if (property.Value is null)
                {
                    continue;
                }

                if (ShouldScrubProperty(property.Key, property.Value, eventType))
                {
                    jsonObject[property.Key] = $"[{property.Key} omitted]";
                    continue;
                }

                ScrubNode(property.Value, eventType);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (JsonNode? child in jsonArray)
            {
                if (child is not null)
                {
                    ScrubNode(child, eventType);
                }
            }
        }
    }

    private static bool ShouldScrubProperty(string propertyName, JsonNode value, string? eventType)
    {
        if (propertyName.Equals("server_sdp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? stringValue))
        {
            return false;
        }

        if (propertyName.Equals("audio", StringComparison.OrdinalIgnoreCase))
        {
            return stringValue.Length > 200;
        }

        if (propertyName.Equals("delta", StringComparison.OrdinalIgnoreCase) &&
            eventType?.Contains("audio", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return propertyName.Contains("sdp", StringComparison.OrdinalIgnoreCase) &&
               !propertyName.Equals("server_sdp", StringComparison.OrdinalIgnoreCase) &&
               stringValue.Length > 200;
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
                    throw new InvalidOperationException("Voice Live Avatar message was too large.");
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

    private static Task SendProxyEventAsync(WebSocket socket, string type, string message, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["message"] = message
        }, JsonOptions);

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

    private sealed record VoiceLiveAvatarSettings(
        string Endpoint,
        string Key,
        string VoiceName,
        string Model,
        string Instructions,
        string AvatarCharacter,
        string AvatarStyle,
        string AvatarBackgroundColor,
        Uri ServiceUri);

    private sealed record WebSocketMessage(WebSocketMessageType MessageType, byte[] Payload);
}
