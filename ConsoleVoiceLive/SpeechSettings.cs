namespace ConsoleVoiceLive;

internal sealed record SpeechSettings(
    string Endpoint,
    string Key,
    string VoiceName,
    string Model,
    string Instructions);
