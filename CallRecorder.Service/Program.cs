using CallRecorder.Core.Config;
using CallRecorder.Service.Audio;
using CallRecorder.Service.Detection;
using CallRecorder.Service.Hosted;
using CallRecorder.Service.Recording;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;
using System.Threading;

var builder = Host.CreateApplicationBuilder(args);

// Single-instance guard to prevent duplicate recorders
bool createdNew;
using var singleInstanceMutex = new System.Threading.Mutex(true, "Global\\CallRecorderService_SingleInstance", out createdNew);
if (!createdNew)
{
    Console.WriteLine("Another instance of CallRecorderService is already running. Exiting.");
    return;
}

// Run as a Windows Service when deployed; harmless when running under VS
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CallRecorderService";
});

// Options from configuration
builder.Services.Configure<RecordingConfig>(builder.Configuration.GetSection("Recording"));
builder.Services.Configure<CallDetectionConfig>(builder.Configuration.GetSection("CallDetection"));
builder.Services.Configure<AudioDspConfig>(builder.Configuration.GetSection("AudioDsp"));
builder.Services.Configure<AudioDeviceConfig>(builder.Configuration.GetSection("AudioDevice"));
builder.Services.Configure<CallStateConfig>(builder.Configuration.GetSection("CallState"));

// Optional one-off echo self-test runner: dotnet run --project CallRecorder.Service -- --echo-test
if (args.Contains("--echo-test"))
{
    var dsp = new AudioDspConfig();
    builder.Configuration.GetSection("AudioDsp").Bind(dsp);

    using var loggerFactory = LoggerFactory.Create(lb => lb.AddConsole().SetMinimumLevel(LogLevel.Information));
    var validatorLogger = loggerFactory.CreateLogger<EchoTestValidator>();
    var validator = new EchoTestValidator(validatorLogger, Options.Create(dsp));
    var result = await validator.RunEchoTestAsync(60);

    Console.WriteLine($"ERLE: {result.ERLE:F1} dB");
    Console.WriteLine($"Residual Leakage: {result.ResidualLeakage:F1} dB");
    Console.WriteLine($"Cross-correlation: {result.CrossCorrelation:F3}");
    Console.WriteLine($"Estimated Delay: {result.EstimatedDelayMs} ms");
    Console.WriteLine($"Report: {result.ReportFile}");

    // Exit after test
    return;
}

// Core services - Using enhanced audio capture with echo cancellation
builder.Services.AddSingleton<IAudioCaptureEngine, EnhancedAudioCaptureEngine>();
builder.Services.AddSingleton<CallDetectionEngine>();
builder.Services.AddSingleton<IAecProcessorFactory, AecProcessorFactory>();
builder.Services.AddSingleton<ICallStateProvider, WindowsAudioSessionCallStateProvider>();
builder.Services.AddSingleton<IRecordingManager, RecordingManager>();

// Orchestrator
builder.Services.AddHostedService<CallRecordingService>();

var host = builder.Build();
host.Run();
