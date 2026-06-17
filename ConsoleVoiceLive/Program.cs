using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Configuration;

namespace ConsoleVoiceLive;

internal class Program
{
    private const string DefaultVoiceName = "en-US-Ava:DragonHDLatestNeural";
    private const string DefaultModel = "gpt-realtime";
    private const string DefaultInstructions = "You are a helpful AI assistant. Respond naturally and conversationally. Keep your responses concise but engaging.";
    private const string PlaceholderEndpoint = "https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com";
    private const string PlaceholderKey = "YOUR_SPEECH_KEY";

    private static async Task<int> Main()
    {
        try
        {
            WriteMenu();

            string? selection = Console.ReadLine();

            return selection switch
            {
                "1" => await RunTextToSpeechAsync(),
                _ => ExitForUnknownMenuSelection(selection)
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void WriteMenu()
    {
        Console.WriteLine("Console Voice Live");
        Console.WriteLine();
        Console.WriteLine("1. Text-To-Speech");
        Console.WriteLine();
        Console.Write("Select an option > ");
    }

    private static async Task<int> RunTextToSpeechAsync()
    {
        SpeechSettings settings = LoadSettings();
        WriteConfiguredSpeechSettings(settings);

        using var speechSynthesizer = CreateSpeechSynthesizer(settings);

        Console.WriteLine("Enter some text that you want to speak >");
        string? text = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine("No text was entered. Nothing to synthesize.");
            return 1;
        }

        SpeechSynthesisResult result = await speechSynthesizer.SpeakTextAsync(text);
        OutputSpeechSynthesisResult(result, text);

        return result.Reason == ResultReason.SynthesizingAudioCompleted ? 0 : 1;
    }

    private static int ExitForUnknownMenuSelection(string? selection)
    {
        Console.WriteLine($"Unknown option: {selection ?? "(blank)"}");
        return 1;
    }

    private static SpeechSettings LoadSettings()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("secrets.appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        string? endpoint = configuration["Speech:Endpoint"];
        string? key = configuration["Speech:Key"];
        string? voiceName = configuration["Speech:VoiceName"];
        string? model = configuration["Speech:Model"];
        string? instructions = configuration["Speech:Instructions"];

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

        return new SpeechSettings(
            endpoint,
            key,
            string.IsNullOrWhiteSpace(voiceName) ? DefaultVoiceName : voiceName,
            string.IsNullOrWhiteSpace(model) ? DefaultModel : model,
            string.IsNullOrWhiteSpace(instructions) ? DefaultInstructions : instructions);
    }

    private static SpeechSynthesizer CreateSpeechSynthesizer(SpeechSettings settings)
    {
        var speechConfig = SpeechConfig.FromEndpoint(new Uri(settings.Endpoint), settings.Key);
        speechConfig.SpeechSynthesisVoiceName = settings.VoiceName;

        return new SpeechSynthesizer(speechConfig);
    }

    private static void WriteConfiguredSpeechSettings(SpeechSettings settings)
    {
        Console.WriteLine("Azure Speech configuration:");
        Console.WriteLine($"  Endpoint: {settings.Endpoint}");
        Console.WriteLine($"  Key: {MaskSecret(settings.Key)}");
        Console.WriteLine($"  Voice: {settings.VoiceName}");
        Console.WriteLine($"  Model: {settings.Model}");
        Console.WriteLine($"  Instructions: {settings.Instructions}");
        Console.WriteLine();
    }

    private static string MaskSecret(string secret)
    {
        const int visibleCharacters = 6;

        if (secret.Length <= visibleCharacters)
        {
            return new string('*', secret.Length);
        }

        return $"{new string('*', secret.Length - visibleCharacters)}{secret[^visibleCharacters..]} (length: {secret.Length})";
    }

    private static void OutputSpeechSynthesisResult(SpeechSynthesisResult result, string text)
    {
        switch (result.Reason)
        {
            case ResultReason.SynthesizingAudioCompleted:
                Console.WriteLine($"Speech synthesized for text: [{text}]");
                break;

            case ResultReason.Canceled:
                SpeechSynthesisCancellationDetails cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");

                if (cancellation.Reason == CancellationReason.Error)
                {
                    Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                    Console.WriteLine($"CANCELED: ErrorDetails=[{cancellation.ErrorDetails}]");
                    Console.WriteLine("CANCELED: Did you set Speech:Endpoint and Speech:Key in secrets.appsettings.json?");
                }

                break;

            default:
                Console.WriteLine($"Speech synthesis completed with result reason: {result.Reason}");
                break;
        }
    }

    private sealed record SpeechSettings(
        string Endpoint,
        string Key,
        string VoiceName,
        string Model,
        string Instructions);
}
