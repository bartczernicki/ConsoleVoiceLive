using System.Globalization;
using System.Net;
using System.Security;
using System.Text.Json;
using Microsoft.CognitiveServices.Speech;

namespace ConsoleLiveWeb.Services;

public sealed class SpeechSynthesisService
{
    private const string DefaultVoiceName = "en-US-Ava:DragonHDLatestNeural";
    private const string DefaultOutputFormat = "Audio24Khz48KBitRateMonoMp3";
    private const string PlaceholderEndpoint = "https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com";
    private const string PlaceholderKey = "YOUR_SPEECH_KEY";
    private const int MinRatePercent = -50;
    private const int MaxRatePercent = 100;
    private const int MinPitchPercent = -50;
    private const int MaxPitchPercent = 50;
    private const int MinVolume = 0;
    private const int MaxVolume = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, SpeechSynthesisOutputFormat> OutputFormatMap =
        new Dictionary<string, SpeechSynthesisOutputFormat>(StringComparer.OrdinalIgnoreCase)
        {
            ["Audio16Khz32KBitRateMonoMp3"] = SpeechSynthesisOutputFormat.Audio16Khz32KBitRateMonoMp3,
            ["Audio16Khz64KBitRateMonoMp3"] = SpeechSynthesisOutputFormat.Audio16Khz64KBitRateMonoMp3,
            ["Audio16Khz128KBitRateMonoMp3"] = SpeechSynthesisOutputFormat.Audio16Khz128KBitRateMonoMp3,
            ["Audio24Khz48KBitRateMonoMp3"] = SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3,
            ["Audio24Khz96KBitRateMonoMp3"] = SpeechSynthesisOutputFormat.Audio24Khz96KBitRateMonoMp3,
            ["Audio24Khz160KBitRateMonoMp3"] = SpeechSynthesisOutputFormat.Audio24Khz160KBitRateMonoMp3,
            ["Audio48Khz96KBitRateMonoMp3"] = SpeechSynthesisOutputFormat.Audio48Khz96KBitRateMonoMp3,
            ["Audio48Khz192KBitRateMonoMp3"] = SpeechSynthesisOutputFormat.Audio48Khz192KBitRateMonoMp3
        };
    private static readonly IReadOnlyList<SpeechOutputFormatOption> OutputFormatOptions =
    [
        new("Audio16Khz32KBitRateMonoMp3", "MP3, 16 kHz, 32 kbps"),
        new("Audio16Khz64KBitRateMonoMp3", "MP3, 16 kHz, 64 kbps"),
        new("Audio16Khz128KBitRateMonoMp3", "MP3, 16 kHz, 128 kbps"),
        new("Audio24Khz48KBitRateMonoMp3", "MP3, 24 kHz, 48 kbps"),
        new("Audio24Khz96KBitRateMonoMp3", "MP3, 24 kHz, 96 kbps"),
        new("Audio24Khz160KBitRateMonoMp3", "MP3, 24 kHz, 160 kbps"),
        new("Audio48Khz96KBitRateMonoMp3", "MP3, 48 kHz, 96 kbps"),
        new("Audio48Khz192KBitRateMonoMp3", "MP3, 48 kHz, 192 kbps")
    ];

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public SpeechSynthesisService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public SpeechSynthesisDefaults GetDefaults()
    {
        return LoadDefaultOptions();
    }

    public IReadOnlyList<SpeechOutputFormatOption> GetOutputFormats()
    {
        return OutputFormatOptions;
    }

    public async Task<SpeechVoiceListResponse> GetVoicesAsync(CancellationToken cancellationToken = default)
    {
        SpeechSynthesisSettings settings;

        try
        {
            settings = LoadSettings();
        }
        catch (InvalidOperationException ex)
        {
            return SpeechVoiceListResponse.Failure(ex.Message);
        }

        using HttpClient httpClient = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildVoicesUri(settings.Endpoint));
        request.Headers.Add("Ocp-Apim-Subscription-Key", settings.Key);

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return SpeechVoiceListResponse.Failure(
                    $"Unable to fetch voices: {(int)response.StatusCode} {response.ReasonPhrase}. {SanitizeServiceMessage(content)}");
            }

            List<AzureVoiceInfo>? azureVoices = JsonSerializer.Deserialize<List<AzureVoiceInfo>>(content, JsonOptions);
            List<SpeechVoiceOption> voices = azureVoices?
                .Select(ToSpeechVoiceOption)
                .Where(voice => !string.IsNullOrWhiteSpace(voice.Name))
                .OrderBy(voice => voice.Locale, StringComparer.OrdinalIgnoreCase)
                .ThenBy(voice => voice.LocalName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            if (voices.Count == 0)
            {
                return SpeechVoiceListResponse.Failure("Azure returned no text-to-speech voices for this resource.");
            }

            return SpeechVoiceListResponse.Success(voices);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return SpeechVoiceListResponse.Failure($"Unable to fetch voices: {ex.Message}");
        }
    }

    public Task<SpeechSynthesisResponse> SynthesizeAsync(string text)
    {
        return SynthesizeAsync(new SpeechSynthesisRequest(text));
    }

    public async Task<SpeechSynthesisResponse> SynthesizeAsync(SpeechSynthesisRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return SpeechSynthesisResponse.Failure("Enter text to synthesize.");
        }

        SpeechSynthesisSettings settings;

        try
        {
            settings = LoadSettings();
        }
        catch (InvalidOperationException ex)
        {
            return SpeechSynthesisResponse.Failure(ex.Message);
        }

        SpeechSynthesisEffectiveOptions options = BuildEffectiveOptions(settings.Defaults, request);
        var speechConfig = SpeechConfig.FromEndpoint(new Uri(settings.Endpoint), settings.Key);
        speechConfig.SpeechSynthesisVoiceName = options.VoiceName;
        speechConfig.SetSpeechSynthesisOutputFormat(OutputFormatMap[options.OutputFormat]);

        using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig: null);
        string ssml = BuildSsml(request.Text, options);
        SpeechSynthesisResult result = await synthesizer.SpeakSsmlAsync(ssml).ConfigureAwait(false);

        return result.Reason switch
        {
            ResultReason.SynthesizingAudioCompleted => SpeechSynthesisResponse.Success(result.AudioData),
            ResultReason.Canceled => SpeechSynthesisResponse.Failure(GetCancellationMessage(result)),
            _ => SpeechSynthesisResponse.Failure($"Speech synthesis completed with unexpected result: {result.Reason}.")
        };
    }

    private SpeechSynthesisSettings LoadSettings()
    {
        string? endpoint = _configuration["Speech:Endpoint"];
        string? key = _configuration["Speech:Key"];

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

        return new SpeechSynthesisSettings(endpoint.Trim(), key.Trim(), LoadDefaultOptions());
    }

    private SpeechSynthesisDefaults LoadDefaultOptions()
    {
        string voiceName = GetString("Speech:VoiceName", DefaultVoiceName);
        string outputFormat = GetString("Speech:TextToSpeech:OutputFormat", DefaultOutputFormat);

        if (!OutputFormatMap.ContainsKey(outputFormat))
        {
            outputFormat = DefaultOutputFormat;
        }

        return new SpeechSynthesisDefaults(
            voiceName,
            ExtractLocale(voiceName),
            outputFormat,
            Clamp(GetInt("Speech:TextToSpeech:RatePercent", 0), MinRatePercent, MaxRatePercent),
            Clamp(GetInt("Speech:TextToSpeech:PitchPercent", 0), MinPitchPercent, MaxPitchPercent),
            Clamp(GetInt("Speech:TextToSpeech:Volume", 100), MinVolume, MaxVolume));
    }

    private string GetString(string key, string fallback)
    {
        string? value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private int GetInt(string key, int fallback)
    {
        return int.TryParse(_configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static SpeechSynthesisEffectiveOptions BuildEffectiveOptions(
        SpeechSynthesisDefaults defaults,
        SpeechSynthesisRequest request)
    {
        string voiceName = string.IsNullOrWhiteSpace(request.VoiceName)
            ? defaults.VoiceName
            : request.VoiceName.Trim();
        string locale = string.IsNullOrWhiteSpace(request.Locale)
            ? ExtractLocale(voiceName)
            : request.Locale.Trim();
        string requestedOutputFormat = request.OutputFormat?.Trim() ?? string.Empty;
        string outputFormat = string.IsNullOrWhiteSpace(requestedOutputFormat) || !OutputFormatMap.ContainsKey(requestedOutputFormat)
            ? defaults.OutputFormat
            : requestedOutputFormat;

        return new SpeechSynthesisEffectiveOptions(
            voiceName,
            locale,
            outputFormat,
            Clamp(request.RatePercent ?? defaults.RatePercent, MinRatePercent, MaxRatePercent),
            Clamp(request.PitchPercent ?? defaults.PitchPercent, MinPitchPercent, MaxPitchPercent),
            Clamp(request.Volume ?? defaults.Volume, MinVolume, MaxVolume));
    }

    private static Uri BuildVoicesUri(string endpoint)
    {
        return new Uri(new Uri(endpoint.TrimEnd('/') + "/"), "tts/cognitiveservices/voices/list");
    }

    private static SpeechVoiceOption ToSpeechVoiceOption(AzureVoiceInfo voice)
    {
        string name = FirstNonEmpty(voice.ShortName, voice.Name);
        string locale = FirstNonEmpty(voice.Locale, ExtractLocale(name));
        string localName = FirstNonEmpty(voice.LocalName, voice.DisplayName, name);
        string gender = FirstNonEmpty(voice.Gender, "Unknown");
        IReadOnlyList<string> styles = voice.StyleList?
            .Where(style => !string.IsNullOrWhiteSpace(style))
            .Select(style => style.Trim())
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return new SpeechVoiceOption(name, locale, localName, gender, styles);
    }

    private static string BuildSsml(string text, SpeechSynthesisEffectiveOptions options)
    {
        string locale = EscapeXml(options.Locale);
        string voiceName = EscapeXml(options.VoiceName);
        string escapedText = EscapeXml(text);
        string rate = FormatPercent(options.RatePercent);
        string pitch = FormatPercent(options.PitchPercent);
        string volume = options.Volume.ToString(CultureInfo.InvariantCulture);

        return $"""<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{locale}"><voice name="{voiceName}"><prosody rate="{rate}" pitch="{pitch}" volume="{volume}">{escapedText}</prosody></voice></speak>""";
    }

    private static string FormatPercent(int value)
    {
        return value > 0
            ? $"+{value.ToString(CultureInfo.InvariantCulture)}%"
            : $"{value.ToString(CultureInfo.InvariantCulture)}%";
    }

    private static string ExtractLocale(string voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return "en-US";
        }

        string[] parts = voiceName.Split('-', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2
            ? $"{parts[0]}-{parts[1]}"
            : "en-US";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string EscapeXml(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
    }

    private static int Clamp(int value, int min, int max)
    {
        return Math.Min(Math.Max(value, min), max);
    }

    private static string SanitizeServiceMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        string message = WebUtility.HtmlDecode(content)
            .ReplaceLineEndings(" ")
            .Trim();

        return message.Length <= 500 ? message : $"{message[..497]}...";
    }

    private static string GetCancellationMessage(SpeechSynthesisResult result)
    {
        SpeechSynthesisCancellationDetails cancellation = SpeechSynthesisCancellationDetails.FromResult(result);

        if (cancellation.Reason != CancellationReason.Error)
        {
            return $"Speech synthesis was canceled: {cancellation.Reason}.";
        }

        return $"Speech synthesis failed: {cancellation.ErrorCode}. {cancellation.ErrorDetails}";
    }

    private sealed record SpeechSynthesisSettings(
        string Endpoint,
        string Key,
        SpeechSynthesisDefaults Defaults);

    private sealed record SpeechSynthesisEffectiveOptions(
        string VoiceName,
        string Locale,
        string OutputFormat,
        int RatePercent,
        int PitchPercent,
        int Volume);

    private sealed class AzureVoiceInfo
    {
        public string? Name { get; init; }

        public string? DisplayName { get; init; }

        public string? LocalName { get; init; }

        public string? ShortName { get; init; }

        public string? Gender { get; init; }

        public string? Locale { get; init; }

        public List<string>? StyleList { get; init; }
    }
}

public sealed record SpeechSynthesisRequest(
    string Text,
    string? VoiceName = null,
    string? Locale = null,
    string? OutputFormat = null,
    int? RatePercent = null,
    int? PitchPercent = null,
    int? Volume = null);

public sealed record SpeechSynthesisDefaults(
    string VoiceName,
    string Locale,
    string OutputFormat,
    int RatePercent,
    int PitchPercent,
    int Volume);

public sealed record SpeechOutputFormatOption(string Value, string Label);

public sealed record SpeechVoiceOption(
    string Name,
    string Locale,
    string LocalName,
    string Gender,
    IReadOnlyList<string> Styles);

public sealed record SpeechVoiceListResponse(
    bool Succeeded,
    IReadOnlyList<SpeechVoiceOption> Voices,
    string? ErrorMessage)
{
    public static SpeechVoiceListResponse Success(IReadOnlyList<SpeechVoiceOption> voices)
    {
        return new SpeechVoiceListResponse(true, voices, null);
    }

    public static SpeechVoiceListResponse Failure(string errorMessage)
    {
        return new SpeechVoiceListResponse(false, [], errorMessage);
    }
}

public sealed record SpeechSynthesisResponse(bool Succeeded, byte[]? AudioData, string? ErrorMessage)
{
    public static SpeechSynthesisResponse Success(byte[] audioData)
    {
        return new SpeechSynthesisResponse(true, audioData, null);
    }

    public static SpeechSynthesisResponse Failure(string errorMessage)
    {
        return new SpeechSynthesisResponse(false, null, errorMessage);
    }
}
