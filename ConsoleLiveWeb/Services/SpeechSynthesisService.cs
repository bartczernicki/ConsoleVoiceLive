using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace ConsoleLiveWeb.Services;

public sealed class SpeechSynthesisService
{
    private const string DefaultVoiceName = "en-US-Ava:DragonHDLatestNeural";
    private const string PlaceholderEndpoint = "https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com";
    private const string PlaceholderKey = "YOUR_SPEECH_KEY";

    private readonly IConfiguration _configuration;

    public SpeechSynthesisService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<SpeechSynthesisResponse> SynthesizeAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
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

        var speechConfig = SpeechConfig.FromEndpoint(new Uri(settings.Endpoint), settings.Key);
        speechConfig.SpeechSynthesisVoiceName = settings.VoiceName;
        speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3);

        using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig: null);
        SpeechSynthesisResult result = await synthesizer.SpeakTextAsync(text).ConfigureAwait(false);

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
        string? voiceName = _configuration["Speech:VoiceName"];

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

        return new SpeechSynthesisSettings(
            endpoint,
            key,
            string.IsNullOrWhiteSpace(voiceName) ? DefaultVoiceName : voiceName);
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

    private sealed record SpeechSynthesisSettings(string Endpoint, string Key, string VoiceName);
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
