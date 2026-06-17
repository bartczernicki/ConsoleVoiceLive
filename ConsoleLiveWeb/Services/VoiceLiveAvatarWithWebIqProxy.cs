using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConsoleLiveWeb.Services;

public sealed class VoiceLiveAvatarWithWebIqProxy
{
    private const string ApiVersion = "2026-04-10";
    private const string DefaultVoiceName = "en-US-Ava:DragonHDLatestNeural";
    private const string DefaultModel = "gpt-realtime-1.5";
    private const string DefaultInstructions = "You are a helpful AI assistant. Respond naturally and conversationally. Keep your responses concise but engaging.";
    private const string DefaultAvatarCharacter = "lisa";
    private const string DefaultAvatarStyle = "casual-sitting";
    private const string DefaultAvatarBackgroundColor = "#FFFFFFFF";
    private const double DefaultVoiceTemperature = 0.8;
    private const double MinVoiceTemperature = 0.6;
    private const double MaxVoiceTemperature = 1.2;
    private const string PlaceholderEndpoint = "https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com";
    private const string PlaceholderKey = "YOUR_SPEECH_KEY";
    private const string WebIqFunctionName = "web_iq_realtime_lookup";
    private const int MaxMessageBytes = 2 * 1024 * 1024;
    private const int MaxLogMessageLength = 1200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string[]> SupportedAvatarStyles =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["lisa"] = ["casual-sitting", "graceful-sitting", "graceful-standing", "technical-sitting", "technical-standing"],
            ["lori"] = ["casual", "graceful", "formal"],
            ["max"] = ["business", "casual"],
            ["harry"] = ["casual", "youthful"]
        };

    private readonly IConfiguration _configuration;
    private readonly WebIqMcpToolService _webIqMcpToolService;
    private readonly ILogger<VoiceLiveAvatarWithWebIqProxy> _logger;

    public VoiceLiveAvatarWithWebIqProxy(
        IConfiguration configuration,
        WebIqMcpToolService webIqMcpToolService,
        ILogger<VoiceLiveAvatarWithWebIqProxy> logger)
    {
        _configuration = configuration;
        _webIqMcpToolService = webIqMcpToolService;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Voice Live Avatar With WebIQ requires a WebSocket request.");
            return;
        }

        using WebSocket browserSocket = await context.WebSockets.AcceptWebSocketAsync();

        VoiceLiveAvatarWithWebIqSettings settings;
        try
        {
            VoiceLiveAvatarRuntimeOptions runtimeOptions = await ReceiveRuntimeOptionsAsync(browserSocket, context.RequestAborted);
            settings = LoadSettings(runtimeOptions);
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
            await SendProxyEventAsync(
                browserSocket,
                "tool.session.update.sent",
                $"{WebIqFunctionName} is the only available tool and tool_choice is required for this avatar session.",
                context.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to connect to Azure Voice Live Avatar With WebIQ endpoint.");
            await SendProxyEventAsync(
                browserSocket,
                "proxy.error",
                $"Unable to connect to Azure Voice Live Avatar With WebIQ. Confirm Speech:Endpoint, Speech:Key, Speech:Model, and avatar region support. {ex.Message}",
                CancellationToken.None);
            await CloseSocketAsync(browserSocket, WebSocketCloseStatus.PolicyViolation, "Azure connection failed");
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var functionCallStates = new Dictionary<string, FunctionCallState>(StringComparer.OrdinalIgnoreCase);
        Task browserToAzure = ProxyBrowserToAzureAsync(browserSocket, azureSocket, linkedCts.Token);
        Task azureToBrowser = ProxyAzureToBrowserAsync(
            azureSocket,
            browserSocket,
            functionCallStates,
            linkedCts.Token);

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
            _logger.LogDebug(ex, "Voice Live Avatar With WebIQ socket closed unexpectedly.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Voice Live Avatar With WebIQ proxy failed.");
            await SendProxyEventAsync(browserSocket, "proxy.error", ex.Message, CancellationToken.None);
        }
        finally
        {
            await CloseSocketAsync(browserSocket, WebSocketCloseStatus.NormalClosure, "Voice Live Avatar With WebIQ stopped");
            await CloseSocketAsync(azureSocket, WebSocketCloseStatus.NormalClosure, "Voice Live Avatar With WebIQ stopped");
        }
    }

    private static async Task<VoiceLiveAvatarRuntimeOptions> ReceiveRuntimeOptionsAsync(
        WebSocket browserSocket,
        CancellationToken cancellationToken)
    {
        WebSocketMessage? message = await ReceiveMessageAsync(browserSocket, cancellationToken);
        if (message is null || message.MessageType != WebSocketMessageType.Text)
        {
            return VoiceLiveAvatarRuntimeOptions.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(message.Payload);
            JsonElement root = document.RootElement;
            if (GetString(root, "type") != "app.voice_live_avatar.configure")
            {
                return VoiceLiveAvatarRuntimeOptions.Empty;
            }

            return new VoiceLiveAvatarRuntimeOptions(
                GetString(root, "avatarCharacter"),
                GetString(root, "avatarStyle"),
                GetString(root, "voiceName"),
                GetNullableDouble(root, "voiceTemperature"),
                GetString(root, "instructions"));
        }
        catch (JsonException)
        {
            return VoiceLiveAvatarRuntimeOptions.Empty;
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

    private async Task ProxyAzureToBrowserAsync(
        ClientWebSocket azureSocket,
        WebSocket browserSocket,
        Dictionary<string, FunctionCallState> functionCallStates,
        CancellationToken cancellationToken)
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
            await HandleFunctionCallEventAsync(
                message,
                azureSocket,
                browserSocket,
                functionCallStates,
                cancellationToken);
        }
    }

    private async Task HandleFunctionCallEventAsync(
        WebSocketMessage message,
        ClientWebSocket azureSocket,
        WebSocket browserSocket,
        Dictionary<string, FunctionCallState> functionCallStates,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(message.Payload);
        JsonElement root = document.RootElement;
        string? type = GetString(root, "type");

        if (type == "conversation.item.created" &&
            root.TryGetProperty("item", out JsonElement item) &&
            GetString(item, "type") == "function_call")
        {
            string? callId = GetString(item, "call_id");
            if (string.IsNullOrWhiteSpace(callId))
            {
                return;
            }

            FunctionCallState state = functionCallStates.GetValueOrDefault(callId) ?? new FunctionCallState();
            state.Name = GetString(item, "name") ?? state.Name;
            state.PreviousItemId = GetString(item, "id") ?? state.PreviousItemId;
            string? itemArguments = GetString(item, "arguments");
            if (!string.IsNullOrWhiteSpace(itemArguments))
            {
                state.Arguments.Clear();
                state.Arguments.Append(itemArguments);
            }

            functionCallStates[callId] = state;
            await SendToolLogAsync(
                browserSocket,
                "tool.requested",
                $"{state.Name ?? WebIqFunctionName} requested. call_id={callId}",
                cancellationToken);
            return;
        }

        if (type is not ("response.function_call_arguments.delta" or "response.function_call_arguments.done"))
        {
            return;
        }

        string? eventCallId = GetString(root, "call_id");
        if (string.IsNullOrWhiteSpace(eventCallId))
        {
            await SendToolLogAsync(browserSocket, "tool.webiq.failed", "Voice Live function call did not include a call_id.", cancellationToken);
            return;
        }

        FunctionCallState eventState = functionCallStates.GetValueOrDefault(eventCallId) ?? new FunctionCallState
        {
            Name = WebIqFunctionName
        };
        functionCallStates[eventCallId] = eventState;

        if (type == "response.function_call_arguments.delta")
        {
            string? delta = GetString(root, "delta");
            if (!string.IsNullOrEmpty(delta))
            {
                eventState.Arguments.Append(delta);
            }

            return;
        }

        if (eventState.Executed)
        {
            return;
        }

        string arguments = GetString(root, "arguments") ?? eventState.Arguments.ToString();
        eventState.Executed = true;
        await ExecuteWebIqFunctionAsync(eventCallId, arguments, azureSocket, browserSocket, cancellationToken);
        functionCallStates.Remove(eventCallId);
    }

    private async Task ExecuteWebIqFunctionAsync(
        string callId,
        string arguments,
        ClientWebSocket azureSocket,
        WebSocket browserSocket,
        CancellationToken cancellationToken)
    {
        string outputJson;

        try
        {
            string query = ExtractQuery(arguments);
            await SendToolLogAsync(browserSocket, "tool.arguments", Truncate(query, MaxLogMessageLength), cancellationToken);
            await SendToolLogAsync(browserSocket, "tool.webiq.started", $"Looking up: {Truncate(query, MaxLogMessageLength)}", cancellationToken);

            WebIqLookupResponse response = await _webIqMcpToolService.LookupAsync(query, cancellationToken);
            if (response.Succeeded)
            {
                outputJson = JsonSerializer.Serialize(new
                {
                    ok = true,
                    query,
                    answer = response.Answer,
                    sources = response.Sources,
                    rawSummary = response.RawSummary
                }, JsonOptions);
                await SendToolLogAsync(browserSocket, "tool.webiq.completed", FormatWebIqCompletion(response), cancellationToken);
            }
            else
            {
                outputJson = JsonSerializer.Serialize(new
                {
                    ok = false,
                    query,
                    error = response.ErrorMessage ?? "WebIQ lookup failed."
                }, JsonOptions);
                await SendToolLogAsync(browserSocket, "tool.webiq.failed", response.ErrorMessage ?? "WebIQ lookup failed.", cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Voice Live Avatar With WebIQ function call failed.");
            outputJson = JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.Message
            }, JsonOptions);
            await SendToolLogAsync(browserSocket, "tool.webiq.failed", ex.Message, cancellationToken);
        }

        await SendTextAsync(azureSocket, BuildFunctionOutput(callId, outputJson), cancellationToken);
        await SendToolLogAsync(browserSocket, "tool.output.sent", $"Function output sent. call_id={callId}", cancellationToken);

        await SendTextAsync(azureSocket, BuildResponseCreate(), cancellationToken);
        await SendToolLogAsync(browserSocket, "tool.response.created", "Requested avatar response using WebIQ output.", cancellationToken);
    }

    private VoiceLiveAvatarWithWebIqSettings LoadSettings(VoiceLiveAvatarRuntimeOptions runtimeOptions)
    {
        string? endpoint = _configuration["Speech:Endpoint"];
        string? key = _configuration["Speech:Key"];
        string? voiceName = _configuration["Speech:VoiceName"];
        string? model = _configuration["Speech:Model"];
        string? instructions = _configuration["Speech:Instructions"];
        string? avatarCharacter = _configuration["Speech:AvatarCharacter"];
        string? avatarStyle = _configuration["Speech:AvatarStyle"];
        string? avatarBackgroundColor = _configuration["Speech:AvatarBackgroundColor"];
        string? avatarBackgroundImageUrl = _configuration["Speech:AvatarBackgroundImageUrl"];

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
        string selectedAvatarCharacter = NormalizeAvatarCharacter(
            FirstNonEmpty(runtimeOptions.AvatarCharacter, avatarCharacter, DefaultAvatarCharacter));
        string selectedAvatarStyle = NormalizeAvatarStyle(
            selectedAvatarCharacter,
            FirstNonEmpty(runtimeOptions.AvatarStyle, avatarStyle, DefaultAvatarStyle));
        double selectedVoiceTemperature = NormalizeVoiceTemperature(runtimeOptions.VoiceTemperature);
        string selectedVoiceName = FirstNonEmpty(runtimeOptions.VoiceName, voiceName, DefaultVoiceName);
        string selectedInstructions = FirstNonEmpty(runtimeOptions.Instructions, instructions, DefaultInstructions);

        return new VoiceLiveAvatarWithWebIqSettings(
            endpoint,
            key,
            selectedVoiceName,
            selectedModel,
            selectedInstructions,
            selectedAvatarCharacter,
            selectedAvatarStyle,
            string.IsNullOrWhiteSpace(avatarBackgroundColor) ? DefaultAvatarBackgroundColor : avatarBackgroundColor,
            NormalizeAvatarBackgroundImageUrl(avatarBackgroundImageUrl),
            selectedVoiceTemperature,
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

    private static string NormalizeAvatarCharacter(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return SupportedAvatarStyles.ContainsKey(normalized)
            ? normalized
            : DefaultAvatarCharacter;
    }

    private static string NormalizeAvatarStyle(string avatarCharacter, string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        string[] supportedStyles = SupportedAvatarStyles[avatarCharacter];
        if (supportedStyles.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return avatarCharacter.Equals(DefaultAvatarCharacter, StringComparison.OrdinalIgnoreCase) &&
               supportedStyles.Contains(DefaultAvatarStyle, StringComparer.OrdinalIgnoreCase)
            ? DefaultAvatarStyle
            : supportedStyles[0];
    }

    private static double NormalizeVoiceTemperature(double? value)
    {
        return Math.Clamp(value ?? DefaultVoiceTemperature, MinVoiceTemperature, MaxVoiceTemperature);
    }

    private static string? NormalizeAvatarBackgroundImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
               uri.Scheme is "http" or "https"
            ? trimmed
            : null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static Dictionary<string, object?> BuildAvatarVideoBackground(VoiceLiveAvatarWithWebIqSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AvatarBackgroundImageUrl))
        {
            return new Dictionary<string, object?>
            {
                ["image_url"] = settings.AvatarBackgroundImageUrl
            };
        }

        return new Dictionary<string, object?>
        {
            ["color"] = settings.AvatarBackgroundColor
        };
    }

    private static string BuildSessionUpdate(VoiceLiveAvatarWithWebIqSettings settings)
    {
        var session = new Dictionary<string, object?>
        {
            ["modalities"] = new[] { "text", "audio" },
            ["instructions"] = BuildInstructions(settings.Instructions),
            ["voice"] = new Dictionary<string, object?>
            {
                ["name"] = settings.VoiceName,
                ["type"] = "azure-standard",
                ["temperature"] = settings.VoiceTemperature
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
                    ["background"] = BuildAvatarVideoBackground(settings)
                }
            },
            ["tools"] = new object[] { BuildWebIqFunctionTool() },
            ["tool_choice"] = "required"
        };

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = session
        }, JsonOptions);
    }

    private static string BuildInstructions(string configuredInstructions)
    {
        return configuredInstructions + "\n\n" +
            $"This avatar session is grounded by the {WebIqFunctionName} function. " +
            "For every user request in this session, call that function before answering. " +
            "Convert the user's spoken question into a concise real-time web query, call the function, then answer naturally from the function output. " +
            "Cite source names or URLs briefly when useful.";
    }

    private static Dictionary<string, object?> BuildWebIqFunctionTool()
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = WebIqFunctionName,
            ["description"] = "Retrieve real-time web intelligence through WebIQ. Use this for current events, recent facts, prices, schedules, releases, product changes, news, or anything likely to have changed recently.",
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["query"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["description"] = "The concise real-time web search query to send to WebIQ."
                    }
                },
                ["required"] = new[] { "query" },
                ["additionalProperties"] = false
            }
        };
    }

    private static string BuildFunctionOutput(string callId, string outputJson)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "conversation.item.create",
            ["item"] = new Dictionary<string, object?>
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = outputJson
            }
        }, JsonOptions);
    }

    private static string BuildResponseCreate()
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "response.create",
            ["response"] = new Dictionary<string, object?>
            {
                ["modalities"] = new[] { "text", "audio" },
                ["tool_choice"] = "none",
                ["instructions"] = "Use the WebIQ function output already present in the conversation to answer the user clearly and concisely. Do not call tools again for this answer. Mention source names or URLs briefly when useful."
            }
        }, JsonOptions);
    }

    private static string ExtractQuery(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            throw new InvalidOperationException("Voice Live function call did not include arguments.");
        }

        using JsonDocument document = JsonDocument.Parse(arguments);
        if (!document.RootElement.TryGetProperty("query", out JsonElement queryElement) ||
            queryElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Voice Live function call arguments must include a query string.");
        }

        string? query = queryElement.GetString();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("Voice Live function call query was empty.");
        }

        return query.Trim();
    }

    private static string FormatWebIqCompletion(WebIqLookupResponse response)
    {
        if (response.Sources.Count == 0)
        {
            return "WebIQ returned a result without explicit sources.";
        }

        string sources = string.Join(
            "; ",
            response.Sources.Take(5).Select(source =>
                string.IsNullOrWhiteSpace(source.Title)
                    ? source.Url
                    : $"{source.Title}: {source.Url}"));

        return $"Sources: {sources}";
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

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double? GetNullableDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out double result)
            ? result
            : null;
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
                    throw new InvalidOperationException("Voice Live Avatar With WebIQ message was too large.");
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
            ["message"] = Truncate(message, MaxLogMessageLength)
        }, JsonOptions);

        return SendTextAsync(socket, payload, cancellationToken);
    }

    private static Task SendToolLogAsync(WebSocket socket, string type, string message, CancellationToken cancellationToken)
    {
        return SendProxyEventAsync(socket, type, message, cancellationToken);
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

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record VoiceLiveAvatarWithWebIqSettings(
        string Endpoint,
        string Key,
        string VoiceName,
        string Model,
        string Instructions,
        string AvatarCharacter,
        string AvatarStyle,
        string AvatarBackgroundColor,
        string? AvatarBackgroundImageUrl,
        double VoiceTemperature,
        Uri ServiceUri);

    private sealed record VoiceLiveAvatarRuntimeOptions(
        string? AvatarCharacter,
        string? AvatarStyle,
        string? VoiceName,
        double? VoiceTemperature,
        string? Instructions)
    {
        public static VoiceLiveAvatarRuntimeOptions Empty { get; } = new(null, null, null, null, null);
    }

    private sealed class FunctionCallState
    {
        public StringBuilder Arguments { get; } = new();

        public string? Name { get; set; }

        public string? PreviousItemId { get; set; }

        public bool Executed { get; set; }
    }

    private sealed record WebSocketMessage(WebSocketMessageType MessageType, byte[] Payload);
}
