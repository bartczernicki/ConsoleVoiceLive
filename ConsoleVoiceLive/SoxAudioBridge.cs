using System.Diagnostics;
using System.Text;

namespace ConsoleVoiceLive;

internal sealed class SoxAudioBridge : IAudioBridge
{
    internal const int SampleRate = 24000;

    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int CaptureChunkBytes = SampleRate * (BitsPerSample / 8) * Channels / 10;

    private readonly SoxCommand _captureCommand;
    private readonly SoxCommand _playbackCommand;
    private readonly SemaphoreSlim _playbackGate = new(1, 1);

    private CancellationTokenSource? _captureCancellationTokenSource;
    private Process? _captureProcess;
    private Process? _playbackProcess;
    private Task? _captureTask;
    private Task? _captureErrorTask;
    private Task? _playbackErrorTask;

    private SoxAudioBridge(SoxCommand captureCommand, SoxCommand playbackCommand)
    {
        _captureCommand = captureCommand;
        _playbackCommand = playbackCommand;
    }

    public static bool TryCreate(out SoxAudioBridge? bridge, out string error)
    {
        string? recPath = FindExecutable("rec");
        string? playPath = FindExecutable("play");

        if (recPath is not null && playPath is not null)
        {
            bridge = new SoxAudioBridge(
                new SoxCommand(recPath, ["-q", .. RawAudioArguments("-")]),
                new SoxCommand(playPath, ["-q", .. RawAudioArguments("-")]));
            error = string.Empty;
            return true;
        }

        string? soxPath = FindExecutable("sox");

        if (soxPath is not null)
        {
            bridge = new SoxAudioBridge(
                new SoxCommand(soxPath, ["-q", "-d", .. RawAudioArguments("-")]),
                new SoxCommand(soxPath, ["-q", .. RawAudioArguments("-"), "-d"]));
            error = string.Empty;
            return true;
        }

        bridge = null;
        error = "SoX is required for VoiceLiveAssistant audio on macOS. Install it with: brew install sox";
        return false;
    }

    public Task StartAsync(Func<byte[], CancellationToken, Task> onAudioCapturedAsync, CancellationToken cancellationToken)
    {
        if (_captureProcess is not null)
        {
            return Task.CompletedTask;
        }

        _captureCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken captureToken = _captureCancellationTokenSource.Token;

        _captureProcess = StartProcess(_captureCommand, redirectStdout: true, redirectStdin: false);
        _captureErrorTask = LogProcessErrorsAsync("capture", _captureProcess, captureToken);
        _captureTask = CaptureAudioAsync(_captureProcess, onAudioCapturedAsync, captureToken);

        return Task.CompletedTask;
    }

    public async Task PlayAsync(byte[] audio, CancellationToken cancellationToken)
    {
        if (audio.Length == 0)
        {
            return;
        }

        await _playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsurePlaybackProcess(cancellationToken);

            if (_playbackProcess is null)
            {
                throw new InvalidOperationException("SoX playback process did not start.");
            }

            await _playbackProcess.StandardInput.BaseStream.WriteAsync(audio, cancellationToken).ConfigureAwait(false);
            await _playbackProcess.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("SoX playback failed. Check that your macOS audio output device is available.", ex);
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    public async Task StopPlaybackAsync()
    {
        await _playbackGate.WaitAsync().ConfigureAwait(false);

        try
        {
            Process? playbackProcess = _playbackProcess;
            Task? playbackErrorTask = _playbackErrorTask;

            _playbackProcess = null;
            _playbackErrorTask = null;

            StopProcess(playbackProcess);

            if (playbackErrorTask is not null)
            {
                await playbackErrorTask.ConfigureAwait(false);
            }

            playbackProcess?.Dispose();
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    public async Task StopAsync()
    {
        _captureCancellationTokenSource?.Cancel();

        Process? captureProcess = _captureProcess;
        Task? captureTask = _captureTask;
        Task? captureErrorTask = _captureErrorTask;

        _captureProcess = null;
        _captureTask = null;
        _captureErrorTask = null;

        StopProcess(captureProcess);

        if (captureTask is not null)
        {
            await captureTask.ConfigureAwait(false);
        }

        if (captureErrorTask is not null)
        {
            await captureErrorTask.ConfigureAwait(false);
        }

        captureProcess?.Dispose();

        await StopPlaybackAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _captureCancellationTokenSource?.Dispose();
        _playbackGate.Dispose();
    }

    private static string[] RawAudioArguments(string streamName)
    {
        return ["-t", "raw", "-b", BitsPerSample.ToString(), "-e", "signed-integer", "-r", SampleRate.ToString(), "-c", Channels.ToString(), streamName];
    }

    private static Process StartProcess(SoxCommand command, bool redirectStdout, bool redirectStdin)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            RedirectStandardError = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardInput = redirectStdin,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start SoX process: {command.FileName}");
    }

    private async Task CaptureAudioAsync(
        Process process,
        Func<byte[], CancellationToken, Task> onAudioCapturedAsync,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[CaptureChunkBytes];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await process.StandardOutput.BaseStream
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                byte[] audio = bytesRead == buffer.Length ? buffer.ToArray() : buffer[..bytesRead].ToArray();
                await onAudioCapturedAsync(audio, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            Console.WriteLine($"SoX capture stopped: {ex.Message}");
        }
    }

    private static async Task LogProcessErrorsAsync(string name, Process process, CancellationToken cancellationToken)
    {
        var errorBuilder = new StringBuilder();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
                    break;
                }

                errorBuilder.AppendLine(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        if (TryGetExitCode(process, out int exitCode) && exitCode != 0 && errorBuilder.Length > 0)
        {
            Console.WriteLine($"SoX {name} exited with code {exitCode}:");
            Console.WriteLine(errorBuilder.ToString().Trim());
        }
    }

    private void EnsurePlaybackProcess(CancellationToken cancellationToken)
    {
        if (_playbackProcess is { HasExited: false })
        {
            return;
        }

        Process? previousPlaybackProcess = _playbackProcess;
        Task? previousPlaybackErrorTask = _playbackErrorTask;

        StopProcess(previousPlaybackProcess);
        previousPlaybackErrorTask?.GetAwaiter().GetResult();
        previousPlaybackProcess?.Dispose();

        _playbackProcess = StartProcess(_playbackCommand, redirectStdout: false, redirectStdin: true);
        _playbackErrorTask = LogProcessErrorsAsync("playback", _playbackProcess, cancellationToken);
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool TryGetExitCode(Process process, out int exitCode)
    {
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        exitCode = 0;
        return false;
    }

    private static string? FindExecutable(string executableName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executableName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record SoxCommand(string FileName, IReadOnlyList<string> Arguments);
}
