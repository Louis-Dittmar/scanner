using Scanner.Models.AiOcr;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Scanner.Services.Interfaces;

/// <summary>
/// Installs and runs the local AI text recognition service (OcrService/server.py, model baidu/Unlimited-OCR).
/// Python, its packages and the model are set up on demand in the app's LocalCache folder. The service starts
/// with the app and stops itself a while after the app has been closed.
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

    #region Settings
    bool IsEnabled { get; set; }
    bool OutputMarkdown { get; set; }
    bool OutputPdf { get; set; }

    /// <summary>
    /// How long the service keeps running after the app has been closed.
    /// </summary>
    int LingerMinutes { get; set; }
    #endregion

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
    /// Analyzes one page image (PNG, JPEG, TIFF or BMP). Call <see cref="EnsureReadyAsync"/> first.
    /// </summary>
    Task<AiOcrPageResult> AnalyzeAsync(byte[] image, CancellationToken cancellationToken = default);
}
