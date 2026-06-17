using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.CognitiveServices.Speech;

namespace ConsoleLiveWeb.Services;

public sealed class TextToVideoAvatarService
{
    private const string DefaultVoiceName = "en-US-Ava:DragonHDLatestNeural";
    private const string DefaultAvatarCharacter = "lisa";
    private const string DefaultAvatarStyle = "casual-sitting";
    private const string DefaultAvatarBackgroundColor = "#FFFFFFFF";
    private const string PlaceholderEndpoint = "https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com";
    private const string PlaceholderKey = "YOUR_SPEECH_KEY";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TextToVideoAvatarService> _logger;
    private readonly ConcurrentDictionary<Guid, AvatarClientSession> _sessions = new();

    public TextToVideoAvatarService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<TextToVideoAvatarService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TextToVideoAvatarIceResponse> GetIceServersAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        SpeechAvatarSettings settings = LoadSettings();
        using HttpClient httpClient = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRelayTokenUri(settings.Endpoint));
        request.Headers.Add("Ocp-Apim-Subscription-Key", settings.Key);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return TextToVideoAvatarIceResponse.Failure(
                $"Unable to fetch avatar relay token: {(int)response.StatusCode} {response.ReasonPhrase}. {SanitizeServiceMessage(content)}");
        }

        try
        {
            List<TextToVideoAvatarIceServer> iceServers = ParseIceServers(content);
            AvatarClientSession session = _sessions.GetOrAdd(clientId, _ => new AvatarClientSession());

            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                session.IceServers = iceServers;
            }
            finally
            {
                session.Gate.Release();
            }

            return TextToVideoAvatarIceResponse.Success(iceServers);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Unable to parse Azure avatar relay token response.");
            return TextToVideoAvatarIceResponse.Failure("Azure returned an avatar relay token response that could not be parsed.");
        }
    }

    public async Task<TextToVideoAvatarConnectResponse> ConnectAsync(
        Guid clientId,
        string localSdpBase64,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localSdpBase64))
        {
            return TextToVideoAvatarConnectResponse.Failure("The browser did not provide a WebRTC offer.");
        }

        SpeechAvatarSettings settings = LoadSettings();
        AvatarClientSession session = _sessions.GetOrAdd(clientId, _ => new AvatarClientSession());

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseConnectionCoreAsync(session).ConfigureAwait(false);

            if (session.IceServers.Count == 0)
            {
                return TextToVideoAvatarConnectResponse.Failure("Fetch avatar relay token before connecting the avatar session.");
            }

            var speechConfig = SpeechConfig.FromEndpoint(BuildAvatarWebSocketUri(settings.Endpoint), settings.Key);
            speechConfig.SpeechSynthesisVoiceName = settings.VoiceName;

            var speechSynthesizer = new SpeechSynthesizer(speechConfig, audioConfig: null);
            Connection connection = Connection.FromSpeechSynthesizer(speechSynthesizer);
            connection.SetMessageProperty(
                "speech.config",
                "context",
                BuildAvatarContextJson(localSdpBase64, session.IceServers, settings));

            session.SpeechSynthesizer = speechSynthesizer;
            session.Connection = connection;
            session.VoiceName = settings.VoiceName;

            SpeechSynthesisResult result = await speechSynthesizer.SpeakTextAsync(string.Empty).ConfigureAwait(false);
            if (result.Reason == ResultReason.Canceled)
            {
                await CloseConnectionCoreAsync(session).ConfigureAwait(false);
                return TextToVideoAvatarConnectResponse.Failure(GetCancellationMessage(result));
            }

            string turnStartMessage = speechSynthesizer.Properties.GetProperty("SpeechSDKInternal-ExtraTurnStartMessage");
            string? remoteSdpBase64 = ExtractRemoteSdp(turnStartMessage);

            if (string.IsNullOrWhiteSpace(remoteSdpBase64))
            {
                await CloseConnectionCoreAsync(session).ConfigureAwait(false);
                return TextToVideoAvatarConnectResponse.Failure("Azure did not return a WebRTC answer for the avatar session.");
            }

            return TextToVideoAvatarConnectResponse.Success(remoteSdpBase64);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to connect avatar session.");
            await CloseConnectionCoreAsync(session).ConfigureAwait(false);
            return TextToVideoAvatarConnectResponse.Failure(ex.Message);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<TextToVideoAvatarSpeakResponse> SpeakTextAsync(
        Guid clientId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TextToVideoAvatarSpeakResponse.Failure("Enter text for the avatar to speak.");
        }

        if (!_sessions.TryGetValue(clientId, out AvatarClientSession? session))
        {
            return TextToVideoAvatarSpeakResponse.Failure("Start an avatar session before speaking.");
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.SpeechSynthesizer is null)
            {
                return TextToVideoAvatarSpeakResponse.Failure("Start an avatar session before speaking.");
            }

            string voiceName = string.IsNullOrWhiteSpace(session.VoiceName)
                ? LoadSettings().VoiceName
                : session.VoiceName;
            string ssml = BuildSsml(text, voiceName);
            SpeechSynthesisResult result = await session.SpeechSynthesizer.SpeakSsmlAsync(ssml).ConfigureAwait(false);

            return result.Reason switch
            {
                ResultReason.SynthesizingAudioCompleted => TextToVideoAvatarSpeakResponse.Success(result.ResultId),
                ResultReason.Canceled => TextToVideoAvatarSpeakResponse.Failure(GetCancellationMessage(result)),
                _ => TextToVideoAvatarSpeakResponse.Failure($"Avatar speech completed with unexpected result: {result.Reason}.")
            };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task StopSpeakingAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(clientId, out AvatarClientSession? session))
        {
            return;
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopSpeakingCoreAsync(session).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task DisconnectAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(clientId, out AvatarClientSession? session))
        {
            return;
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseConnectionCoreAsync(session).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
            session.Gate.Dispose();
        }
    }

    private SpeechAvatarSettings LoadSettings()
    {
        string? endpoint = _configuration["Speech:Endpoint"];
        string? key = _configuration["Speech:Key"];
        string? voiceName = _configuration["Speech:VoiceName"];
        string? avatarCharacter = _configuration["Speech:AvatarCharacter"];
        string? avatarStyle = _configuration["Speech:AvatarStyle"];
        string? avatarBackgroundColor = _configuration["Speech:AvatarBackgroundColor"];

        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == PlaceholderEndpoint)
        {
            throw new InvalidOperationException(
                "Set Speech:Endpoint in secrets.appsettings.json to your Azure Speech endpoint.");
        }

        if (string.IsNullOrWhiteSpace(key) || key == PlaceholderKey)
        {
            throw new InvalidOperationException(
                "Set Speech:Key in secrets.appsettings.json to your Azure Speech resource key.");
        }

        return new SpeechAvatarSettings(
            endpoint,
            key,
            string.IsNullOrWhiteSpace(voiceName) ? DefaultVoiceName : voiceName,
            string.IsNullOrWhiteSpace(avatarCharacter) ? DefaultAvatarCharacter : avatarCharacter,
            string.IsNullOrWhiteSpace(avatarStyle) ? DefaultAvatarStyle : avatarStyle,
            string.IsNullOrWhiteSpace(avatarBackgroundColor) ? DefaultAvatarBackgroundColor : avatarBackgroundColor);
    }

    private static Uri BuildRelayTokenUri(string endpoint)
    {
        return new Uri(new Uri(endpoint.TrimEnd('/') + "/"), "tts/cognitiveservices/avatar/relay/token/v1");
    }

    private static Uri BuildAvatarWebSocketUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
        {
            throw new InvalidOperationException("Speech:Endpoint must be an absolute Azure endpoint URI.");
        }

        var builder = new UriBuilder(endpointUri)
        {
            Scheme = Uri.UriSchemeWss,
            Port = -1,
            Path = "tts/cognitiveservices/websocket/v1",
            Query = "enableTalkingAvatar=true"
        };

        return builder.Uri;
    }

    private static List<TextToVideoAvatarIceServer> ParseIceServers(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        JsonElement urlsElement = GetRequiredProperty(root, "Urls");
        string[] urls = urlsElement.ValueKind switch
        {
            JsonValueKind.Array => urlsElement.EnumerateArray()
                .Select(item => item.GetString())
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!)
                .ToArray(),
            JsonValueKind.String => [urlsElement.GetString()!],
            _ => []
        };

        string[] turnUrls = urls
            .Where(url => url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string[] selectedUrls = turnUrls.Length > 0 ? turnUrls : urls;
        if (selectedUrls.Length == 0)
        {
            throw new JsonException("Avatar relay token did not include ICE URLs.");
        }

        string username = GetRequiredProperty(root, "Username").GetString() ?? string.Empty;
        string credential = GetRequiredProperty(root, "Password").GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(credential))
        {
            throw new JsonException("Avatar relay token did not include ICE credentials.");
        }

        return [new TextToVideoAvatarIceServer(selectedUrls, username, credential)];
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement value))
        {
            return value;
        }

        string camelName = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (element.TryGetProperty(camelName, out value))
        {
            return value;
        }

        throw new JsonException($"Missing {propertyName}.");
    }

    private static string BuildAvatarContextJson(
        string localSdpBase64,
        IReadOnlyList<TextToVideoAvatarIceServer> iceServers,
        SpeechAvatarSettings settings)
    {
        var avatarConfig = new
        {
            synthesis = new
            {
                video = new
                {
                    protocol = new
                    {
                        name = "WebRTC",
                        webrtcConfig = new
                        {
                            clientDescription = localSdpBase64,
                            iceServers = iceServers.Select(iceServer => new
                            {
                                urls = iceServer.Urls,
                                username = iceServer.Username,
                                credential = iceServer.Credential
                            })
                        }
                    },
                    format = new
                    {
                        resolution = new
                        {
                            width = 1920,
                            height = 1080
                        },
                        bitrate = 1000000
                    },
                    talkingAvatar = new
                    {
                        customized = false,
                        character = settings.AvatarCharacter,
                        style = settings.AvatarStyle,
                        background = new
                        {
                            color = settings.AvatarBackgroundColor
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(avatarConfig, JsonOptions);
    }

    private static string? ExtractRemoteSdp(string turnStartMessage)
    {
        if (string.IsNullOrWhiteSpace(turnStartMessage))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(turnStartMessage);
        if (!document.RootElement.TryGetProperty("webrtc", out JsonElement webrtcElement) ||
            !webrtcElement.TryGetProperty("connectionString", out JsonElement connectionStringElement))
        {
            return null;
        }

        return connectionStringElement.GetString();
    }

    private static string BuildSsml(string text, string voiceName)
    {
        string locale = GetLocaleFromVoiceName(voiceName);
        string escapedText = WebUtility.HtmlEncode(text);

        return $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{locale}">
              <voice name="{voiceName}">{escapedText}</voice>
            </speak>
            """;
    }

    private static string GetLocaleFromVoiceName(string voiceName)
    {
        string[] parts = voiceName.Split('-', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "en-US";
    }

    private static string GetCancellationMessage(SpeechSynthesisResult result)
    {
        SpeechSynthesisCancellationDetails cancellation = SpeechSynthesisCancellationDetails.FromResult(result);

        if (cancellation.Reason != CancellationReason.Error)
        {
            return $"Avatar speech was canceled: {cancellation.Reason}.";
        }

        return $"Avatar speech failed: {cancellation.ErrorCode}. {cancellation.ErrorDetails}";
    }

    private static async Task StopSpeakingCoreAsync(AvatarClientSession session)
    {
        if (session.Connection is not null)
        {
            await session.Connection.SendMessageAsync("synthesis.control", "{\"action\":\"stop\"}").ConfigureAwait(false);
        }
    }

    private async Task CloseConnectionCoreAsync(AvatarClientSession session)
    {
        try
        {
            await StopSpeakingCoreAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ApplicationException)
        {
            _logger.LogDebug(ex, "Avatar stop speaking failed during cleanup.");
        }

        try
        {
            session.Connection?.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ApplicationException)
        {
            _logger.LogDebug(ex, "Avatar connection close failed during cleanup.");
        }

        session.Connection?.Dispose();
        session.SpeechSynthesizer?.Dispose();
        session.Connection = null;
        session.SpeechSynthesizer = null;
    }

    private static string SanitizeServiceMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        return message.Length > 600 ? message[..600] : message;
    }

    private sealed record SpeechAvatarSettings(
        string Endpoint,
        string Key,
        string VoiceName,
        string AvatarCharacter,
        string AvatarStyle,
        string AvatarBackgroundColor);

    private sealed class AvatarClientSession
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IReadOnlyList<TextToVideoAvatarIceServer> IceServers { get; set; } = [];

        public SpeechSynthesizer? SpeechSynthesizer { get; set; }

        public Connection? Connection { get; set; }

        public string? VoiceName { get; set; }
    }
}

public sealed record TextToVideoAvatarIceServer(IReadOnlyList<string> Urls, string Username, string Credential);

public sealed record TextToVideoAvatarIceResponse(
    bool Succeeded,
    IReadOnlyList<TextToVideoAvatarIceServer> IceServers,
    string? ErrorMessage)
{
    public static TextToVideoAvatarIceResponse Success(IReadOnlyList<TextToVideoAvatarIceServer> iceServers)
    {
        return new TextToVideoAvatarIceResponse(true, iceServers, null);
    }

    public static TextToVideoAvatarIceResponse Failure(string errorMessage)
    {
        return new TextToVideoAvatarIceResponse(false, [], errorMessage);
    }
}

public sealed record TextToVideoAvatarConnectResponse(bool Succeeded, string? RemoteSdpBase64, string? ErrorMessage)
{
    public static TextToVideoAvatarConnectResponse Success(string remoteSdpBase64)
    {
        return new TextToVideoAvatarConnectResponse(true, remoteSdpBase64, null);
    }

    public static TextToVideoAvatarConnectResponse Failure(string errorMessage)
    {
        return new TextToVideoAvatarConnectResponse(false, null, errorMessage);
    }
}

public sealed record TextToVideoAvatarSpeakResponse(bool Succeeded, string? ResultId, string? ErrorMessage)
{
    public static TextToVideoAvatarSpeakResponse Success(string resultId)
    {
        return new TextToVideoAvatarSpeakResponse(true, resultId, null);
    }

    public static TextToVideoAvatarSpeakResponse Failure(string errorMessage)
    {
        return new TextToVideoAvatarSpeakResponse(false, null, errorMessage);
    }
}
