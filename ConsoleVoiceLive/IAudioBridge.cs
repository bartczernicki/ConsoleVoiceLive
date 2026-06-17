namespace ConsoleVoiceLive;

internal interface IAudioBridge : IAsyncDisposable
{
    Task StartAsync(Func<byte[], CancellationToken, Task> onAudioCapturedAsync, CancellationToken cancellationToken);

    Task PlayAsync(byte[] audio, CancellationToken cancellationToken);

    Task StopPlaybackAsync();

    Task StopAsync();
}
