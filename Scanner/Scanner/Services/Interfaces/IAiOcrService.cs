using Scanner.Models.AiOcr;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Scanner.Services.Interfaces;

/// <summary>
/// Installs and runs the local AI text recognition service (OcrService/server.py, model baidu/Unlimited-OCR).
/// Python, its packages and the model are set up on demand, by default in the app's LocalCache folder (removed by
/// Windows together with the app) or in a folder the user picked. The service, and with it the model, starts with
/// the app and stops when the app is closed.
/// </summary>
/// <remarks>
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> may be raised on any thread.
/// </remarks>
public interface IAiOcrService : INotifyPropertyChanged
{
    bool IsSupported { get; }
    bool IsInstalled { get; }

    AiOcrState State { get; }
    string StatusText { get; }

    /// <summary>
    /// 0..1 while installing, <see langword="null"/> if the progress is unknown.
    /// </summary>
    double? Progress { get; }

    /// <summary>
    /// Details of the last error, e.g. the end of the installer output or the service's error message.
    /// </summary>
    string? ErrorDetails { get; }

    /// <summary>
    /// Where Python, packages, model and logs are stored.
    /// </summary>
    string InstallFolderPath { get; }

    /// <summary>
    /// Whether <see cref="InstallFolderPath"/> is inside the app's data (removed with the app).
    /// </summary>
    bool IsDefaultInstallLocation { get; }

    #region Settings
    bool IsEnabled { get; set; }

    /// <summary>
    /// How long the service keeps running after the app has been closed, 0 = it stops with the app.
    /// </summary>
    int LingerMinutes { get; set; }

    /// <summary>
    /// Unload the model after this many idle minutes while the app runs, 0 = never.
    /// </summary>
    int IdleUnloadMinutes { get; set; }

    /// <summary>
    /// The user doesn't want to be asked to set up the AI text recognition on launch.
    /// </summary>
    bool SetupPromptDismissed { get; set; }
    #endregion

    /// <summary>
    /// Installs into a subfolder of <paramref name="parentFolder"/> from now on, <see langword="null"/> for the
    /// default location. Only allowed while nothing is installed.
    /// </summary>
    void SetInstallLocation(string? parentFolder);

    /// <summary>
    /// Downloads and installs Python and all packages. The model itself is downloaded on the first start.
    /// </summary>
    Task InstallAsync(CancellationToken cancellationToken = default);

    Task UninstallAsync();

    /// <summary>
    /// Starts the service if it's enabled and installed, without waiting for the model. Called on app launch.
    /// </summary>
    Task StartIfEnabledAsync();

    /// <summary>
    /// Connects to a running service or starts it and waits until the model is ready.
    /// </summary>
    /// <returns><see langword="true"/> if the service is ready.</returns>
    Task<bool> EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task StopAsync();

    /// <summary>
    /// Stops the service right away when the app closes (unless <see cref="LingerMinutes"/> keeps it), blocking
    /// for at most a few seconds. The service also notices a closed app by itself.
    /// </summary>
    void ShutdownForAppExit();

    /// <summary>
    /// Analyzes one page image (PNG or JPEG) and returns the raw model output with its position markers, to be
    /// parsed with Scanner.Core's <c>UnlimitedOcrParser</c>. Call <see cref="EnsureReadyAsync"/> first.
    /// </summary>
    Task<string> AnalyzeRawAsync(byte[] image, CancellationToken cancellationToken = default);
}
