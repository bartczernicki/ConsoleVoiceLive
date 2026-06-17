using Azure;
using Azure.AI.VoiceLive;

namespace ConsoleVoiceLive;

internal sealed class VoiceLiveAssistant
{
    private readonly SpeechSettings _settings;
    private readonly IAudioBridge _audioBridge;

    private bool _responseActive;
    private bool _canCancelResponse;

    public VoiceLiveAssistant(SpeechSettings settings, IAudioBridge audioBridge)
    {
        _settings = settings;
        _audioBridge = audioBridge;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var credential = new AzureKeyCredential(_settings.Key);
        var options = new VoiceLiveClientOptions(VoiceLiveClientOptions.ServiceVersion.V2026_01_01_PREVIEW);
        var client = new VoiceLiveClient(new Uri(_settings.Endpoint), credential, options);

        await using VoiceLiveSession session = await client
            .StartSessionAsync(_settings.Model, cancellationToken)
            .ConfigureAwait(false);

        await ConfigureSessionAsync(session, cancellationToken).ConfigureAwait(false);
        await _audioBridge
            .StartAsync((audio, token) => session.SendInputAudioAsync(audio, token), cancellationToken)
            .ConfigureAwait(false);

        WriteReadyMessage();

        try
        {
            await ProcessUpdatesAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _audioBridge.StopAsync().ConfigureAwait(false);
            await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ConfigureSessionAsync(VoiceLiveSession session, CancellationToken cancellationToken)
    {
        var sessionOptions = new VoiceLiveSessionOptions
        {
            Model = _settings.Model,
            Instructions = _settings.Instructions,
            Voice = new AzureStandardVoice(_settings.VoiceName),
            InputAudioSamplingRate = SoxAudioBridge.SampleRate,
            InputAudioEchoCancellation = new AudioEchoCancellation(),
            InputAudioFormat = InputAudioFormat.Pcm16,
            OutputAudioFormat = OutputAudioFormat.Pcm16,
            TurnDetection = new ServerVadTurnDetection
            {
                Threshold = 0.5f,
                PrefixPadding = TimeSpan.FromMilliseconds(400),
                SilenceDuration = TimeSpan.FromMilliseconds(500)
            }
        };

        sessionOptions.Modalities.Clear();
        sessionOptions.Modalities.Add(InteractionModality.Text);
        sessionOptions.Modalities.Add(InteractionModality.Audio);

        await session.ConfigureSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessUpdatesAsync(VoiceLiveSession session, CancellationToken cancellationToken)
    {
        await foreach (SessionUpdate serverEvent in session.GetUpdatesAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (serverEvent)
            {
                case SessionUpdateSessionCreated sessionCreated:
                    Console.WriteLine($"Session ready: {sessionCreated.Session?.Id}");
                    break;

                case SessionUpdateSessionUpdated:
                    Console.WriteLine("Session configured. Start speaking.");
                    break;

                case SessionUpdateInputAudioBufferSpeechStarted:
                    Console.WriteLine("User started speaking.");
                    await HandleUserSpeechStartedAsync(session, cancellationToken).ConfigureAwait(false);
                    break;

                case SessionUpdateInputAudioBufferSpeechStopped:
                    Console.WriteLine("Processing...");
                    break;

                case SessionUpdateResponseCreated:
                    _responseActive = true;
                    _canCancelResponse = true;
                    break;

                case SessionUpdateResponseTextDelta textDelta:
                    Console.Write(textDelta.Delta);
                    break;

                case SessionUpdateResponseAudioDelta audioDelta when audioDelta.Delta is not null:
                    await _audioBridge.PlayAsync(audioDelta.Delta.ToArray(), cancellationToken).ConfigureAwait(false);
                    break;

                case SessionUpdateResponseAudioDone:
                    Console.WriteLine();
                    Console.WriteLine("Ready for next input...");
                    break;

                case SessionUpdateResponseDone:
                    _responseActive = false;
                    _canCancelResponse = false;
                    break;

                case SessionUpdateError errorEvent:
                    _responseActive = false;
                    _canCancelResponse = false;
                    Console.WriteLine($"Voice Live error: {errorEvent.Error?.Message}");
                    break;
            }
        }
    }

    private async Task HandleUserSpeechStartedAsync(VoiceLiveSession session, CancellationToken cancellationToken)
    {
        await _audioBridge.StopPlaybackAsync().ConfigureAwait(false);

        if (!_responseActive || !_canCancelResponse)
        {
            return;
        }

        try
        {
            await session.CancelResponseAsync(cancellationToken).ConfigureAwait(false);
            await session.ClearStreamingAudioAsync(cancellationToken).ConfigureAwait(false);
            _canCancelResponse = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or RequestFailedException)
        {
            Console.WriteLine($"Unable to cancel current response: {ex.Message}");
        }
    }

    private static void WriteReadyMessage()
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine(" VOICE LIVE ASSISTANT READY");
        Console.WriteLine("Start speaking to begin conversation");
        Console.WriteLine("Press Ctrl+C to exit");
        Console.WriteLine("============================================================");
        Console.WriteLine();
    }
}
