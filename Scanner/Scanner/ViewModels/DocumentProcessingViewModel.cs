using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Scanner.Core.Pdf;
using Scanner.Messages;
using Scanner.Models.AiOcr;
using Scanner.Models.Paperless;
using Scanner.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Scanner.ViewModels;

/// <summary>
/// The document window: follows <see cref="IDocumentPipelineService.CurrentJob"/> and offers what can be done with it.
/// </summary>
public partial class DocumentProcessingViewModel : ObservableObject, IDisposable
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Services
    public readonly IDocumentPipelineService PipelineService = Ioc.Default.GetRequiredService<IDocumentPipelineService>();
    public readonly IAiOcrService AiOcrService = Ioc.Default.GetRequiredService<IAiOcrService>();
    private readonly IPaperlessService PaperlessService = Ioc.Default.GetRequiredService<IPaperlessService>();
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private DocumentJob? observedJob;

    public DocumentJob? Job => PipelineService.CurrentJob;
    public bool HasJob => Job != null;
    public bool HasNoJob => Job == null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPage), nameof(PageCounterText), nameof(CanGoBack), nameof(CanGoForward))]
    private DocumentJobPage? selectedPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRegionItem))]
    private DocumentJobRegion? selectedRegion;

    /// <summary>
    /// <see cref="SelectedRegion"/> for the list's two-way binding, which is typed as object.
    /// </summary>
    public object? SelectedRegionItem
    {
        get => SelectedRegion;
        set => SelectedRegion = value as DocumentJobRegion;
    }

    [ObservableProperty]
    private bool showRegions = true;

    [ObservableProperty]
    private bool showOriginal;

    /// <summary>
    /// 0 = text, 1 = regions, 2 = PDF.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextTab), nameof(IsRegionsTab), nameof(IsPdfTab))]
    private int selectedTab;

    public bool IsTextTab => SelectedTab == 0;
    public bool IsRegionsTab => SelectedTab == 1;
    public bool IsPdfTab => SelectedTab == 2;

    public bool HasSelectedPage => SelectedPage != null;
    public string PageCounterText => SelectedPage == null || Job == null ? "" : string.Format(Resources.Strings.Resources.DocumentPageCounter, SelectedPage.PageNumber, Job.Pages.Count);
    public bool CanGoBack => SelectedPage != null && Job != null && Job.Pages.IndexOf(SelectedPage) > 0;
    public bool CanGoForward => SelectedPage != null && Job != null && Job.Pages.IndexOf(SelectedPage) < Job.Pages.Count - 1;
    public bool IsPaperlessConfigured => PaperlessService.IsConfigured;
    public bool IsModelLoading => AiOcrService.State is AiOcrState.Starting or AiOcrState.DownloadingModel or AiOcrState.LoadingModel;
    public string ModelStatusText => AiOcrService.StatusText;
    public string OutputFolderPath => PipelineService.OutputFolderPath;

    #region Options
    public bool WhiteCorrection
    {
        get => PipelineService.WhiteCorrection;
        set
        {
            if (PipelineService.WhiteCorrection == value)
                return;
            PipelineService.WhiteCorrection = value;
            OnPropertyChanged();
            OptionsChanged = true;
        }
    }

    public bool CropToDocument
    {
        get => PipelineService.CropToDocument;
        set
        {
            if (PipelineService.CropToDocument == value)
                return;
            PipelineService.CropToDocument = value;
            OnPropertyChanged();
            OptionsChanged = true;
        }
    }

    /// <summary>
    /// 0 = reconstructed, 1 = scan with text layer. Switching only creates the PDF again.
    /// </summary>
    public int PdfModeIndex
    {
        get => PipelineService.PdfMode == PdfOutputMode.Reconstructed ? 0 : 1;
        set
        {
            PdfOutputMode mode = value == 0 ? PdfOutputMode.Reconstructed : PdfOutputMode.ScanWithTextLayer;
            if (PipelineService.PdfMode == mode)
                return;
            PipelineService.PdfMode = mode;
            OnPropertyChanged();
            if (Job is { IsDone: true })
                _ = PipelineService.RecomposeAsync();
        }
    }

    /// <summary>
    /// White correction or cropping changed since the last run, so processing again makes a difference.
    /// </summary>
    [ObservableProperty]
    private bool optionsChanged;
    #endregion


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public DocumentProcessingViewModel()
    {
        PipelineService.PropertyChanged += PipelineService_PropertyChanged;
        AiOcrService.PropertyChanged += AiOcrService_PropertyChanged;
        ObserveJob();
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public void Dispose()
    {
        PipelineService.PropertyChanged -= PipelineService_PropertyChanged;
        AiOcrService.PropertyChanged -= AiOcrService_PropertyChanged;
        if (observedJob != null)
        {
            observedJob.Pages.CollectionChanged -= Pages_CollectionChanged;
            observedJob.PropertyChanged -= Job_PropertyChanged;
        }
    }

    private void PipelineService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IDocumentPipelineService.CurrentJob):
                ObserveJob();
                break;
            case nameof(IDocumentPipelineService.OutputFolderPath):
                OnPropertyChanged(nameof(OutputFolderPath));
                break;
        }
    }

    private void AiOcrService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Refresh()
        {
            OnPropertyChanged(nameof(IsModelLoading));
            OnPropertyChanged(nameof(ModelStatusText));
        }

        if (dispatcherQueue == null || dispatcherQueue.HasThreadAccess)
            Refresh();
        else
            dispatcherQueue.TryEnqueue(Refresh);
    }

    private void ObserveJob()
    {
        if (observedJob != null)
        {
            observedJob.Pages.CollectionChanged -= Pages_CollectionChanged;
            observedJob.PropertyChanged -= Job_PropertyChanged;
        }

        // keep the page the user looked at when the same document is processed again
        int previousIndex = SelectedPage?.PageNumber - 1 ?? 0;
        observedJob = Job;
        OptionsChanged = false;
        if (observedJob != null)
        {
            observedJob.Pages.CollectionChanged += Pages_CollectionChanged;
            observedJob.PropertyChanged += Job_PropertyChanged;
        }

        SelectedPage = observedJob?.Pages.Count > 0 ? observedJob.Pages[Math.Clamp(previousIndex, 0, observedJob.Pages.Count - 1)] : null;
        OnPropertyChanged(nameof(Job));
        OnPropertyChanged(nameof(HasJob));
        OnPropertyChanged(nameof(HasNoJob));
        OnPropertyChanged(nameof(IsPaperlessConfigured));
    }

    private void Pages_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (Job == null)
            return;
        if (SelectedPage == null || !Job.Pages.Contains(SelectedPage))
            SelectedPage = Job.Pages.Count > 0 ? Job.Pages[0] : null;
        OnPropertyChanged(nameof(PageCounterText));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // show the finished PDF as soon as it exists
        if (e.PropertyName == nameof(DocumentJob.State) && Job is { IsDone: true })
            OptionsChanged = false;
    }

    partial void OnShowOriginalChanged(bool value)
    {
        if (Job == null)
            return;
        foreach (DocumentJobPage page in Job.Pages)
            page.ShowOriginal = value;
    }

    partial void OnSelectedRegionChanged(DocumentJobRegion? oldValue, DocumentJobRegion? newValue)
    {
        if (oldValue != null)
            oldValue.IsHighlighted = false;
        if (newValue != null)
            newValue.IsHighlighted = true;
    }

    partial void OnSelectedPageChanged(DocumentJobPage? value)
    {
        SelectedRegion = null;
        if (value != null)
            value.ShowOriginal = ShowOriginal;
    }

    #region Commands
    [RelayCommand]
    private void PreviousPage()
    {
        if (Job != null && SelectedPage != null && CanGoBack)
            SelectedPage = Job.Pages[Job.Pages.IndexOf(SelectedPage) - 1];
    }

    [RelayCommand]
    private void NextPage()
    {
        if (Job != null && SelectedPage != null && CanGoForward)
            SelectedPage = Job.Pages[Job.Pages.IndexOf(SelectedPage) + 1];
    }

    [RelayCommand]
    private Task ProcessCurrentProjectAsync() => PipelineService.ProcessCurrentProjectAsync();

    [RelayCommand]
    private Task ReprocessAsync()
    {
        OptionsChanged = false;
        return PipelineService.ReprocessAsync();
    }

    [RelayCommand]
    private void Cancel() => PipelineService.Cancel();

    [RelayCommand]
    private async Task OpenPdfAsync()
    {
        if (Job?.OutputPdfPath is not string path || !File.Exists(path))
            return;
        LogService?.Log.Information("Opening created PDF");
        await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        string folder = Job?.OutputFolderPath ?? OutputFolderPath;
        Directory.CreateDirectory(folder);

        FolderLauncherOptions options = new();
        if (Job?.OutputPdfPath is string path && File.Exists(path))
            options.ItemsToSelect.Add(await StorageFile.GetFileFromPathAsync(path));
        await Launcher.LaunchFolderPathAsync(folder, options);
    }

    [RelayCommand]
    private void CopyText()
    {
        string text = SelectedPage?.Markdown is { Length: > 0 } pageText ? pageText : Job?.Markdown ?? "";
        if (text.Length == 0)
            return;
        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    [RelayCommand]
    private void OpenAiSettings()
    {
        WeakReferenceMessenger.Default.Send(new ShowSettingsMessage(new SettingsViewModelIntent(SettingsPageType.AiOcr)));
    }

    [RelayCommand]
    private void SetUpAi()
    {
        WeakReferenceMessenger.Default.Send(new ShowAiSetupDialogMessage());
    }

    [RelayCommand]
    private async Task SendToPaperlessAsync()
    {
        if (Job?.OutputPdfPath is not string path || !File.Exists(path))
            return;

        if (!PaperlessService.IsConfigured)
        {
            WeakReferenceMessenger.Default.Send(new ShowSettingsMessage(new SettingsViewModelIntent(SettingsPageType.Paperless)));
            return;
        }

        byte[] content = await File.ReadAllBytesAsync(path);
        List<PaperlessUploadFile> files = [new PaperlessUploadFile(Path.GetFileName(path), content, "application/pdf")];
        WeakReferenceMessenger.Default.Send(new ShowPaperlessUploadDialogMessage(files, Job.Title));
    }
    #endregion
}
