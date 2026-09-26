using System.Diagnostics;
using System.Reflection;
using IsTranscribe.Host.Audio;
using IsTranscribe.Host.Audio.Capture;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Host.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

return await AudioSmokeProgram.RunAsync(args);

internal static class AudioSmokeProgram
{
    private const int DeviceAndMicDurationSeconds = 3;
    private const int ProcessLoopbackDurationSeconds = 3;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--emit-tone", StringComparison.OrdinalIgnoreCase))
        {
            return await RunToneEmitterAsync(args);
        }

        var outputRoot = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            "artifacts",
            "audio-smoke",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));

        Directory.CreateDirectory(outputRoot);
        var logger = new BootstrapFileLogger(Path.Combine(outputRoot, "smoke.log"));
        var capability = new WindowsCapabilityAssessor(new HostOperatingSystemDetector()).Assess();
        var foundationCapability = AudioFoundationCapabilitySnapshot.FromHostCapability(capability);
        var engine = new AudioRecorderEngine(logger);
        engine.UpdateCapabilitySnapshot(foundationCapability);

        using var enumerator = new MMDeviceEnumerator();
        using var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var defaultMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

        Console.WriteLine($"Output root: {outputRoot}");
        Console.WriteLine($"Host capability: {capability.State}, ProcessLoopbackAvailable={capability.ProcessLoopbackAvailable}");
        Console.WriteLine($"Default render: {defaultRender.FriendlyName}");
        Console.WriteLine($"Default mic: {defaultMic.FriendlyName}");

        var failures = new List<string>();

        try
        {
            await RunDeviceAndMicScenarioAsync(outputRoot, engine, defaultRender.ID, defaultMic.ID);
        }
        catch (Exception exception)
        {
            failures.Add($"device+mic: {exception.Message}");
            Console.WriteLine($"[FAIL] device+mic: {exception}");
        }

        if (foundationCapability.ProcessLoopbackSupported)
        {
            try
            {
                await RunProcessLoopbackScenarioAsync(outputRoot, engine);
            }
            catch (Exception exception)
            {
                failures.Add($"process_output: {exception.Message}");
                Console.WriteLine($"[FAIL] process_output: {exception}");
            }
        }
        else
        {
            Console.WriteLine("[SKIP] process_output: process loopback is unsupported on this host.");
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("Smoke pass completed successfully.");
            return 0;
        }

        Console.WriteLine("Smoke pass failed:");
        foreach (var failure in failures)
        {
            Console.WriteLine($" - {failure}");
        }

        return 1;
    }

    private static async Task<int> RunToneEmitterAsync(string[] args)
    {
        var durationSeconds = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5;
        var signal = new SignalGenerator()
        {
            Gain = 0.2,
            Frequency = 440,
            Type = SignalGeneratorType.Sin
        };

        using var output = new WaveOutEvent();
        output.Init(signal.ToWaveProvider16());
        output.Play();
        await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
        output.Stop();
        return 0;
    }

    private static async Task RunDeviceAndMicScenarioAsync(
        string outputRoot,
        AudioRecorderEngine engine,
        string renderDeviceId,
        string micDeviceId)
    {
        var scenarioRoot = Path.Combine(outputRoot, "device-and-mic");
        Directory.CreateDirectory(scenarioRoot);

        using var toneProcess = StartToneEmitter(DeviceAndMicDurationSeconds + 2);
        await Task.Delay(500);

        var request = new AudioCaptureRequest(
            Guid.NewGuid(),
            AudioCaptureMode.Manual,
            [
                AudioCaptureSourceRequest.DeviceLoopback(renderDeviceId),
                AudioCaptureSourceRequest.Microphone(micDeviceId)
            ],
            scenarioRoot,
            PrebufferSeconds: 0,
            CreateMixedArtifact: true);

        await using var session = await engine.StartAsync(request, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(DeviceAndMicDurationSeconds));
        await session.StopAsync(CancellationToken.None);
        await toneProcess.WaitForExitAsync();

        var outputArtifact = session.Snapshot.Artifacts.SingleOrDefault(artifact => artifact.Kind == AudioCaptureArtifactKind.Output)
            ?? throw new InvalidOperationException("device+mic scenario did not produce output.wav.");
        var micArtifact = session.Snapshot.Artifacts.SingleOrDefault(artifact => artifact.Kind == AudioCaptureArtifactKind.Microphone)
            ?? throw new InvalidOperationException("device+mic scenario did not produce mic.wav.");
        var mixArtifact = session.Snapshot.Artifacts.SingleOrDefault(artifact => artifact.Kind == AudioCaptureArtifactKind.Mixed)
            ?? throw new InvalidOperationException("device+mic scenario did not produce mix.wav.");

        var outputPeak = ReadPeak(outputArtifact.Path);
        if (outputPeak < 0.01f)
        {
            throw new InvalidOperationException($"device+mic output.wav peak is too low ({outputPeak}).");
        }

        EnsureWaveReadable(micArtifact.Path);
        EnsureWaveReadable(mixArtifact.Path);

        Console.WriteLine($"[OK] device+mic: outputPeak={outputPeak:F4}; output={outputArtifact.Path}; mic={micArtifact.Path}; mix={mixArtifact.Path}");
    }

    private static async Task RunProcessLoopbackScenarioAsync(string outputRoot, AudioRecorderEngine engine)
    {
        var scenarioRoot = Path.Combine(outputRoot, "process-output");
        Directory.CreateDirectory(scenarioRoot);

        using var toneProcess = StartToneEmitter(ProcessLoopbackDurationSeconds + 2);
        await Task.Delay(500);

        var request = new AudioCaptureRequest(
            Guid.NewGuid(),
            AudioCaptureMode.Manual,
            [AudioCaptureSourceRequest.ProcessOutput(toneProcess.Id, "IsTranscribe.AudioSmoke")],
            scenarioRoot,
            PrebufferSeconds: 0,
            CreateMixedArtifact: false);

        await using var session = await engine.StartAsync(request, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(ProcessLoopbackDurationSeconds));
        await session.StopAsync(CancellationToken.None);
        await toneProcess.WaitForExitAsync();

        var outputArtifact = session.Snapshot.Artifacts.SingleOrDefault(artifact => artifact.Kind == AudioCaptureArtifactKind.Output)
            ?? throw new InvalidOperationException("process_output scenario did not produce output.wav.");

        var outputPeak = ReadPeak(outputArtifact.Path);
        if (outputPeak < 0.01f)
        {
            throw new InvalidOperationException($"process_output output.wav peak is too low ({outputPeak}).");
        }

        Console.WriteLine($"[OK] process_output: outputPeak={outputPeak:F4}; output={outputArtifact.Path}");
    }

    private static Process StartToneEmitter(int durationSeconds)
    {
        var assemblyPath = Assembly.GetEntryAssembly()?.Location
            ?? Path.Combine(AppContext.BaseDirectory, "IsTranscribe.AudioSmoke.dll");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { assemblyPath, "--emit-tone", durationSeconds.ToString() },
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start tone emitter process.");

        return process;
    }

    private static float ReadPeak(string path)
    {
        using var reader = new WaveFileReader(path);
        var samples = reader.ToSampleProvider();
        var buffer = new float[samples.WaveFormat.SampleRate * samples.WaveFormat.Channels];
        var peak = 0f;
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                peak = Math.Max(peak, Math.Abs(buffer[index]));
            }
        }

        return peak;
    }

    private static void EnsureWaveReadable(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.Length <= 0)
        {
            throw new InvalidOperationException($"Wave artifact '{path}' is empty.");
        }
    }
}
