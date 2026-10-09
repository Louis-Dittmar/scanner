using Microsoft.UI.Dispatching;
using Scanner.Core.Pdf;
using Scanner.ViewModels;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Scanner.Services.Interfaces;

/// <summary>
/// Turns the current project into a finished document: detects the paper, makes it white, recognizes the text with
/// the local AI (<see cref="IAiOcrService"/>), creates a fully selectable PDF and saves it to the output folder.
/// The processing logic itself lives in Scanner.Core (<c>DocumentProcessor</c>); this service feeds it the project's
/// saved pages and publishes the progress as a <see cref="DocumentJob"/> for the document window.
/// </summary>
/// <remarks>
/// Runs automatically once a new scan has been saved (<see cref="AutoProcessAfterScan"/>) or on request.
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> is raised on the UI thread.
/// </remarks>
public interface IDocumentPipelineService : INotifyPropertyChanged
{
    /// <summary>
    /// The document currently or last processed.
    /// </summary>
    DocumentJob? CurrentJob { get; }

    #region Settings
    bool AutoProcessAfterScan { get; set; }
    bool WhiteCorrection { get; set; }
    bool CropToDocument { get; set; }
    PdfOutputMode PdfMode { get; set; }
    bool WriteMarkdown { get; set; }

    /// <summary>
    /// Where finished PDFs are saved. Later also the starting point for sending them to Paperless-ngx.
    /// </summary>
    string OutputFolderPath { get; set; }
    string DefaultOutputFolderPath { get; }
    #endregion

    /// <summary>
    /// Starts watching for new scans. Called once on app launch.
    /// </summary>
    void Initialize(DispatcherQueue uiDispatcherQueue);

    /// <summary>
    /// Processes the saved current project and shows the document window.
    /// </summary>
    Task ProcessCurrentProjectAsync();

    /// <summary>
    /// Runs the current job again with the current settings (recognition results are cached).
    /// </summary>
    Task ReprocessAsync();

    /// <summary>
    /// Only creates the PDF again, e.g. after switching <see cref="PdfMode"/>.
    /// </summary>
    Task RecomposeAsync();

    void Cancel();
}
