using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimCineCapture.Capture.Services;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Models;
using SimCineCapture.SimConnect.Services;
using SimCineCapture.UI.Services;
using System.Windows;

namespace SimCineCapture.UI;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<AppSettings>(context.Configuration);

                services.AddSingleton<ISimConnectService, MsfsSimConnectService>();

                services.AddSingleton<MainWindow>();

                services.AddSingleton<IAppSettingsPersistenceService, JsonAppSettingsPersistenceService>();

                services.AddSingleton<DummyCaptureBackend>();
                services.AddSingleton<DesktopDuplicationCaptureBackend>();

                services.AddTransient<PngSequenceFrameSink>();
                services.AddSingleton<IRecordingFrameSinkFactory, ConfiguredRecordingFrameSinkFactory>();
                services.AddSingleton<ISequenceVideoEncoder, FfmpegImageSequenceVideoEncoder>();

                services.AddSingleton<ICaptureBackend>(sp =>
                {
                    var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
                    var backend = settings.Recorder.CaptureBackend?.Trim().ToLowerInvariant();

                    return backend switch
                    {
                        "desktopduplication" => sp.GetRequiredService<DesktopDuplicationCaptureBackend>(),
                        "desktop-duplication" => sp.GetRequiredService<DesktopDuplicationCaptureBackend>(),
                        "dxgi" => sp.GetRequiredService<DesktopDuplicationCaptureBackend>(),
                        _ => sp.GetRequiredService<DummyCaptureBackend>()
                    };
                });

                services.AddSingleton<IRecorderService, RecorderService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();

        base.OnExit(e);
    }
}