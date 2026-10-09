using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Scanner.Core.Imaging;
using Scanner.Core.Pdf;
using Scanner.Core.Pipeline;
using Scanner.Messages;
using Scanner.Models;
using Scanner.Models.AiOcr;
using Scanner.Services.Interfaces;
using Scanner.Services.Pipeline;
using Scanner.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Scanner.Services;

internal sealed partial class DocumentPipelineService : ObservableObject, IDocumentPipelineService
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Services
    private readonly IProjectService ProjectService = Ioc.Default.GetRequiredService<IProjectService>();
    private readonly IAiOcrService AiOcrService = Ioc.Default.GetRequiredService<IAiOcrService>();
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    #region Constants
    private const string settingsContainerName = "DocumentPipeline";

    /// <summary>
    /// Wait this long after the last change following a scan before processing it.
    /// </summary>
    private static readonly TimeSpan debounceDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A scan only triggers the automatic processing if its project is ready within this time.
    /// </summary>
    private static readonly TimeSpan autoProcessWindow = TimeSpan.FromMinutes(10);
    #endregion

    private DispatcherQueue? uiDispatcherQueue;
    private DateTime? autoProcessRequested;
    private CancellationTokenSource? pendingTrigger;
    private CancellationTokenSource? jobCancellation;
    private readonly SemaphoreSlim jobSemaphore = new(1, 1);

    // the last processed document, for running it again or creating its PDF again
    private List<DecodedPage>? lastInputs;
    private string? lastTitle;
    private string? lastOutputPath;
    private List<ProcessedPage>? lastPages;
    private DocumentProcessor? lastProcessor;

    [ObservableProperty]
    private DocumentJob? currentJob;

    public string DefaultOutputFolderPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scanner Paperless");

    #region Settings
    public bool AutoProcessAfterScan
    {
        get => GetSetting(nameof(AutoProcessAfterScan), true);
        set => SetSetting(nameof(AutoProcessAfterScan), value);
    }

    public bool WhiteCorrection
    {
        get => GetSetting(nameof(WhiteCorrection), true);
        set => SetSetting(nameof(WhiteCorrection), value);
    }

    public bool CropToDocument
    {
        get => GetSetting(nameof(CropToDocument), true);
        set => SetSetting(nameof(CropToDocument), value);
    }

    public PdfOutputMode PdfMode
    {
        get => (PdfOutputMode)GetSetting(nameof(PdfMode), (int)PdfOutputMode.Reconstructed);
        set => SetSetting(nameof(PdfMode), (int)value);
    }

    public bool WriteMarkdown
    {
        get => GetSetting(nameof(WriteMarkdown), false);
        set => SetSetting(nameof(WriteMarkdown), value);
    }

    public string OutputFolderPath
    {
        get
        {
            if (App.IsUiTestMode)
                return Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "UiTestOutput");
            string value = GetSetting(nameof(OutputFolderPath), "");
            return string.IsNullOrWhiteSpace(value) ? DefaultOutputFolderPath : value;
        }
        set => SetSetting(nameof(OutputFolderPath), value == DefaultOutputFolderPath ? "" : value ?? "");
    }
    #endregion


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Settings helpers
    private static Windows.Storage.ApplicationDataContainer SettingsContainer =>
        ApplicationData.Current.LocalSettings.CreateContainer(settingsContainerName, ApplicationDataCreateDisposition.Always);

    private static T GetSetting<T>(string name, T defaultValue) =>
        SettingsContainer.Values[name] is T value ? value : defaultValue;

    private void SetSetting<T>(string name, T value)
    {
        if (SettingsContainer.Values[name] is T current && EqualityComparer<T>.Default.Equals(current, value))
            return;
        SettingsContainer.Values[name] = value;
        RunOnUi(() => OnPropertyChanged(name));
    }
    #endregion

    #region Triggers
    public void Initialize(DispatcherQueue uiDispatcherQueue)
    {
        this.uiDispatcherQueue = uiDispatcherQueue;
        ProjectService.ScanCompletedSuccessfully += ProjectService_ScanCompletedSuccessfully;
        ProjectService.PropertyChanged += ProjectService_PropertyChanged;
    }

    private void ProjectService_ScanCompletedSuccessfully(object? sender, EventArgs e)
    {
        if (!AutoProcessAfterScan || !(AiOcrService.IsInstalled || App.IsUiTestMode))
            return;

        // the scan still becomes (part of) a project and gets rotated etc., so wait until all that is done
        LogService?.Log.Information("Scan completed, document will be processed automatically");
        autoProcessRequested = DateTime.UtcNow;
        ScheduleAutoProcess();
    }

    private void ProjectService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IProjectService.CurrentProject):
            case nameof(IProjectService.IsProcessRunning):
                if (autoProcessRequested != null)
                    ScheduleAutoProcess();
                break;
        }
    }

    /// <summary>
    /// Runs the automatic processing shortly after the scan's project is ready (debounced, so the steps after a scan
    /// like rotating and adding to the project lead to a single run).
    /// </summary>
    private void ScheduleAutoProcess()
    {
        pendingTrigger?.Cancel();
        CancellationTokenSource trigger = new();
        pendingTrigger = trigger;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounceDelay, trigger.Token);
                if (autoProcessRequested is not DateTime requested || DateTime.UtcNow - requested > autoProcessWindow)
                    return;
                if (ProjectService.IsProcessRunning || ProjectService.CurrentProject == null)
                    return;     // checked again once the process has finished

                autoProcessRequested = null;
                await RunAsync(automatic: true);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer change
            }
            catch (Exception exc)
            {
                LogService?.Log.Error(exc, "Automatic document processing failed");
            }
        });
    }
    #endregion

    #region Jobs
    public Task ProcessCurrentProjectAsync() => RunAsync(automatic: false);

    public void Cancel()
    {
        jobCancellation?.Cancel();
    }

    private async Task RunAsync(bool automatic)
    {
        ProjectBase? project = ProjectService.CurrentProject;
        if (project == null)
            return;

        CancellationToken cancellationToken = StartNewJobCancellation();
        await jobSemaphore.WaitAsync();
        try
        {
            // the saved file is the most faithful source (ink, rotation and effects applied); a fresh scan that
            // hasn't been saved yet is read from its pages
            List<SavedProjectFile>? files = project.IsSaved
                ? await project.TryReadSavedFilesAsync(notifyIfNotSaved: false, waitForSave: !automatic)
                : null;
            string title = SanitizeFileName(files?.Count > 0
                ? Path.GetFileNameWithoutExtension(files[0].FileName)
                : $"Scan {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
            LogService?.Log.Information("Processing document (saved: {IsSaved}, automatic: {Automatic})", files?.Count > 0, automatic);

            DocumentJob job = await CreateJobAsync(title);
            RunOnUi(() => WeakReferenceMessenger.Default.Send(new ShowDocumentWindowMessage()));

            List<DecodedPage> inputs = [];
            try
            {
                if (files?.Count > 0)
                {
                    foreach (SavedProjectFile file in files)
                        inputs.AddRange(await WinRtImaging.DecodeFileAsync(file.FileName, file.Content));
                }
                else
                {
                    foreach (ProjectPageImage page in await project.ReadPageImagesAsync())
                        inputs.Add(await WinRtImaging.DecodeImageAsync(page.Content, page.Rotation));
                }

                if (inputs.Count == 0)
                    throw new InvalidDataException("The project has no pages");
            }
            catch (Exception exc)
            {
                LogService?.Log.Error(exc, "Failed to read the project's pages");
                await FailAsync(job, Resources.Strings.Resources.DocumentErrorReadingPages, exc.Message);
                return;
            }

            lastInputs = inputs;
            lastTitle = title;
            lastOutputPath = null;
            await ProcessInputsAsync(job, inputs, title, cancellationToken);
        }
        finally
        {
            jobSemaphore.Release();
        }
    }

    public async Task ReprocessAsync()
    {
        if (lastInputs == null || lastTitle == null)
            return;

        CancellationToken cancellationToken = StartNewJobCancellation();
        await jobSemaphore.WaitAsync();
        try
        {
            DocumentJob job = await CreateJobAsync(lastTitle);
            await ProcessInputsAsync(job, lastInputs, lastTitle, cancellationToken);
        }
        finally
        {
            jobSemaphore.Release();
        }
    }

    public async Task RecomposeAsync()
    {
        if (lastPages == null || lastProcessor == null || CurrentJob is not DocumentJob job || lastTitle == null)
            return;

        await jobSemaphore.WaitAsync();
        try
        {
            DocumentProcessingOptions options = CreateOptions(lastTitle);
            await OnUiAsync(() =>
            {
                job.State = DocumentJobState.Saving;
                job.ErrorText = null;
                job.SetActiveStep(job.StepPdf);
                job.StatusText = Resources.Strings.Resources.DocumentStatusComposing;
            });

            List<ProcessedPage> pages = lastPages;
            DocumentProcessor processor = lastProcessor;
            byte[] pdf = await Task.Run(() => processor.ComposePdf(pages, options));
            await SaveAsync(job, pdf, DocumentProcessor.CreateMarkdown(pages));
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Creating the PDF again failed");
            await FailAsync(job, Resources.Strings.Resources.DocumentErrorPdf, exc.Message);
        }
        finally
        {
            jobSemaphore.Release();
        }
    }

    private CancellationToken StartNewJobCancellation()
    {
        jobCancellation?.Cancel();
        jobCancellation = new CancellationTokenSource();
        return jobCancellation.Token;
    }

    private async Task<DocumentJob> CreateJobAsync(string title)
    {
        DocumentJob? job = null;
        await OnUiAsync(() =>
        {
            job = new DocumentJob(title);
            job.SetActiveStep(job.StepLoad);
            job.StatusText = Resources.Strings.Resources.DocumentStatusLoading;
            CurrentJob = job;
        });
        return job!;
    }

    private DocumentProcessingOptions CreateOptions(string title) => new()
    {
        WhiteCorrection = WhiteCorrection,
        CropToDocument = CropToDocument,
        PdfMode = PdfMode,
        Title = title,
    };

    private async Task ProcessInputsAsync(DocumentJob job, List<DecodedPage> inputs, string title, CancellationToken cancellationToken)
    {
        lastPages = null;
        lastProcessor = null;

        try
        {
            // 1. show the pages right away
            List<ProcessedPage> pages = DocumentProcessor.CreatePages(inputs.Select(x => (x.Image, x.Dpi)));
            List<BgraImage> previews = [];
            foreach (ProcessedPage page in pages)
                previews.Add(await WinRtImaging.PrepareForDisplayAsync(page.Input));

            Dictionary<ProcessedPage, DocumentJobPage> pageViews = [];
            await OnUiAsync(() =>
            {
                job.Pages.Clear();
                for (int i = 0; i < pages.Count; i++)
                {
                    DocumentJobPage view = new(pages[i])
                    {
                        OriginalImage = WinRtImaging.ToBitmap(previews[i]),
                        DisplayWidth = previews[i].Width,
                        DisplayHeight = previews[i].Height,
                        StepText = Resources.Strings.Resources.DocumentPageWaiting,
                    };
                    job.Pages.Add(view);
                    pageViews[pages[i]] = view;
                }
            });

            // 2. is the recognition available?
            IOcrEngine engine;
            if (App.IsUiTestMode)
            {
                engine = new UiTestOcrEngine();
            }
            else if (!AiOcrService.IsSupported)
            {
                await FailAsync(job, Resources.Strings.Resources.AiOcrStateUnsupported, null);
                return;
            }
            else if (!AiOcrService.IsInstalled)
            {
                await OnUiAsync(() =>
                {
                    job.State = DocumentJobState.NeedsSetup;
                    job.StatusText = Resources.Strings.Resources.DocumentStatusNeedsSetup;
                    job.Progress = 0;
                });
                return;
            }
            else
            {
                engine = new AiOcrEngine();
            }

            // 3. white correction, recognition and PDF
            await OnUiAsync(() => job.State = DocumentJobState.Processing);
            DocumentProcessor processor = new(engine, new WinRtImageEncoder());
            ProgressReporter reporter = new(this, job, pageViews, pages.Count);

            void AiOcrService_PropertyChanged(object? sender, PropertyChangedEventArgs e) => reporter.UpdateModelStatus();
            AiOcrService.PropertyChanged += AiOcrService_PropertyChanged;
            DocumentProcessingResult result;
            try
            {
                result = await processor.ProcessAsync(pages, CreateOptions(title), reporter, cancellationToken);
            }
            finally
            {
                AiOcrService.PropertyChanged -= AiOcrService_PropertyChanged;
            }

            lastPages = pages;
            lastProcessor = processor;
            await reporter.FlushAsync();

            // 4. save
            await SaveAsync(job, result.Pdf, result.Markdown);
        }
        catch (OperationCanceledException)
        {
            LogService?.Log.Information("Document processing cancelled");
            await OnUiAsync(() =>
            {
                job.State = DocumentJobState.Cancelled;
                job.FailActiveStep();
                job.StatusText = Resources.Strings.Resources.DocumentStatusCancelled;
            });
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Document processing failed");
            await FailAsync(job, Resources.Strings.Resources.DocumentErrorProcessing, exc.Message);
        }
    }

    private async Task SaveAsync(DocumentJob job, byte[] pdf, string markdown)
    {
        await OnUiAsync(() =>
        {
            job.State = DocumentJobState.Saving;
            job.SetActiveStep(job.StepSave);
            job.StatusText = Resources.Strings.Resources.DocumentStatusSaving;
            job.Markdown = markdown;
        });

        string folder = OutputFolderPath;
        (string pdfPath, string? markdownPath) = await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            string path = lastOutputPath != null && Path.GetDirectoryName(lastOutputPath) == folder
                ? lastOutputPath
                : GetFreePath(folder, lastTitle ?? job.Title, ".pdf");
            File.WriteAllBytes(path, pdf);

            string? mdPath = null;
            if (WriteMarkdown)
            {
                mdPath = Path.ChangeExtension(path, ".md");
                File.WriteAllText(mdPath, markdown);
            }
            return (path, mdPath);
        });
        lastOutputPath = pdfPath;

        LogService?.Log.Information("Document saved ({Pages} page(s), mode {Mode})", job.Pages.Count, PdfMode);
        await OnUiAsync(() =>
        {
            job.OutputPdfPath = pdfPath;
            job.OutputMarkdownPath = markdownPath;
            job.State = DocumentJobState.Done;
            job.CompleteAllSteps();
            job.Progress = 1;
            job.StatusText = string.Format(Resources.Strings.Resources.DocumentStatusDone, Path.GetFileName(pdfPath));
            WeakReferenceMessenger.Default.Send(new ShowInAppNotificationMessage(new CommunityToolkit.WinUI.Behaviors.Notification
            {
                Title = Resources.Strings.Resources.DocumentDoneHeading,
                Message = string.Format(Resources.Strings.Resources.DocumentStatusDone, Path.GetFileName(pdfPath)),
                Severity = InfoBarSeverity.Success,
            }));
        });
    }

    private async Task FailAsync(DocumentJob job, string message, string? details)
    {
        await OnUiAsync(() =>
        {
            job.State = DocumentJobState.Failed;
            job.FailActiveStep();
            job.StatusText = message;
            job.ErrorText = details;
        });
    }

    internal static string GetFreePath(string folder, string name, string extension)
    {
        string path = Path.Combine(folder, name + extension);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}){extension}");
        return path;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "Scan" : name;
    }
    #endregion

    #region UI thread
    private void RunOnUi(Action action)
    {
        if (uiDispatcherQueue == null || uiDispatcherQueue.HasThreadAccess)
            action();
        else
            uiDispatcherQueue.TryEnqueue(() => action());
    }

    private Task OnUiAsync(Action action)
    {
        if (uiDispatcherQueue == null || uiDispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = uiDispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exc)
            {
                completion.SetException(exc);
            }
        });
        if (!queued)
            completion.SetException(new InvalidOperationException("The UI thread is gone"));
        return completion.Task;
    }
    #endregion

    /// <summary>
    /// Forwards the pipeline's progress to the job on the UI thread, preparing preview bitmaps on the way.
    /// </summary>
    private sealed class ProgressReporter(DocumentPipelineService service, DocumentJob job,
        Dictionary<ProcessedPage, DocumentJobPage> pageViews, int pageCount) : IProgress<DocumentProcessingProgress>
    {
        private readonly object sync = new();
        private readonly Dictionary<ProcessedPage, BgraImage> shownProcessed = [];
        private Task pending = Task.CompletedTask;
        private DocumentProcessingProgress? last;

        public void Report(DocumentProcessingProgress value)
        {
            // keep the order of the updates, each one may have to prepare a bitmap first
            lock (sync)
            {
                last = value;
                ProcessingStep step = value.Step;
                pending = pending.ContinueWith(_ => ApplyAsync(value.Page, step), TaskScheduler.Default).Unwrap();
            }
        }

        public Task FlushAsync()
        {
            lock (sync)
                return pending;
        }

        public void UpdateModelStatus()
        {
            DocumentProcessingProgress? current;
            lock (sync)
                current = last;
            if (current?.Step == ProcessingStep.Recognizing)
                service.RunOnUi(() => job.StatusText = GetStatusText(current.Page, current.Step));
        }

        private async Task ApplyAsync(ProcessedPage page, ProcessingStep step)
        {
            try
            {
                // a new processed image (after white correction, again after protecting pictures) needs a new preview
                BgraImage? processed = page.Processed;
                BgraImage? preview = null;
                if (processed != null && step is ProcessingStep.Recognizing or ProcessingStep.Recognized
                    && (!shownProcessed.TryGetValue(page, out BgraImage? shown) || !ReferenceEquals(shown, processed)))
                {
                    shownProcessed[page] = processed;
                    preview = await WinRtImaging.PrepareForDisplayAsync(processed);
                }

                await service.OnUiAsync(() =>
                {
                    if (!pageViews.TryGetValue(page, out DocumentJobPage? view))
                        return;

                    view.Step = step;
                    view.StepText = GetPageStepText(page, step);
                    if (preview != null)
                    {
                        view.ProcessedImage = WinRtImaging.ToBitmap(preview);
                        view.DisplayWidth = preview.Width;
                        view.DisplayHeight = preview.Height;
                        view.DetectionText = GetDetectionText(page);
                    }
                    if (step == ProcessingStep.Recognized)
                    {
                        view.Page = page;
                        view.SetRegions(page.Ocr);
                        view.Markdown = page.Ocr?.Markdown ?? "";
                    }

                    switch (step)
                    {
                        case ProcessingStep.DetectingDocument:
                        case ProcessingStep.WhiteCorrection:
                            job.SetActiveStep(job.StepWhite);
                            break;
                        case ProcessingStep.Recognizing:
                            job.SetActiveStep(job.StepRecognize);
                            break;
                        case ProcessingStep.ComposingPdf:
                            job.SetActiveStep(job.StepPdf);
                            break;
                    }
                    job.Progress = GetProgress(page, step);
                    job.StatusText = GetStatusText(page, step);
                });
            }
            catch (Exception exc)
            {
                service.LogService?.Log.Warning(exc, "Failed to show document progress");
            }
        }

        private double GetProgress(ProcessedPage page, ProcessingStep step)
        {
            // three units per page (white correction, recognition, done), then the PDF and saving
            double units = pageCount * 3 + 2;
            double done = step switch
            {
                ProcessingStep.DetectingDocument or ProcessingStep.WhiteCorrection => page.Index * 3,
                ProcessingStep.Recognizing => page.Index * 3 + 1,
                ProcessingStep.Recognized => page.Index * 3 + 3,
                ProcessingStep.ComposingPdf => pageCount * 3,
                ProcessingStep.Done => pageCount * 3 + 1,
                _ => page.Index * 3,
            };
            return Math.Clamp(done / units, 0, 1);
        }

        private string GetStatusText(ProcessedPage page, ProcessingStep step)
        {
            if (step == ProcessingStep.ComposingPdf || step == ProcessingStep.Done)
                return Resources.Strings.Resources.DocumentStatusComposing;

            string pageText = string.Format(Resources.Strings.Resources.DocumentStatusPage, page.Index + 1, pageCount);
            if (step == ProcessingStep.Recognizing && !App.IsUiTestMode && service.AiOcrService.State != AiOcrState.Ready)
                return $"{pageText} · {service.AiOcrService.StatusText}";
            return $"{pageText} · {GetPageStepText(page, step)}";
        }

        private static string GetPageStepText(ProcessedPage page, ProcessingStep step) => step switch
        {
            ProcessingStep.DetectingDocument => Resources.Strings.Resources.DocumentPageDetecting,
            ProcessingStep.WhiteCorrection => Resources.Strings.Resources.DocumentPageWhite,
            ProcessingStep.Recognizing => Resources.Strings.Resources.DocumentPageRecognizing,
            ProcessingStep.Recognized or ProcessingStep.ComposingPdf or ProcessingStep.Done =>
                string.Format(Resources.Strings.Resources.DocumentPageRecognized, page.Ocr?.Regions.Count ?? 0, page.RecognitionDuration?.TotalSeconds ?? 0),
            ProcessingStep.Failed => page.Error ?? "",
            _ => Resources.Strings.Resources.DocumentPageWaiting,
        };

        private static string GetDetectionText(ProcessedPage page)
        {
            if (page.Detection is not { IsDocument: true } detection)
                return Resources.Strings.Resources.DocumentDetectionNone;
            bool cropped = detection.IsCropped(page.Input.Width, page.Input.Height) && !ReferenceEquals(page.Original, page.Input);
            if (!page.IsWhiteCorrected)
                return cropped ? Resources.Strings.Resources.DocumentDetectionCropped : Resources.Strings.Resources.DocumentDetectionFound;
            return cropped ? Resources.Strings.Resources.DocumentDetectionCroppedWhite : Resources.Strings.Resources.DocumentDetectionWhite;
        }
    }
}
