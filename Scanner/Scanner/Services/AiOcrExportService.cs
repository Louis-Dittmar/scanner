using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Scanner.Messages;
using Scanner.Models;
using Scanner.Models.AiOcr;
using Scanner.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Scanner.Services;

internal class AiOcrExportService : IAiOcrExportService
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Services
    private readonly IAiOcrService AiOcrService = Ioc.Default.GetRequiredService<IAiOcrService>();
    private readonly IProjectService ProjectService = Ioc.Default.GetRequiredService<IProjectService>();
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    #region Constants
    /// <summary>
    /// Wait for this long after a save, so a burst of auto-saves leads to a single export.
    /// </summary>
    private static readonly TimeSpan debounceDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan errorNotificationInterval = TimeSpan.FromMinutes(10);
    private const double pdfRenderDpi = 200;
    private const string aiPdfSuffix = " (KI)";
    #endregion

    private DispatcherQueue? uiDispatcherQueue;
    private ProjectBase? watchedProject;
    private CancellationTokenSource? pendingExport;
    private readonly SemaphoreSlim exportSemaphore = new(1, 1);
    private DateTime lastErrorNotification = DateTime.MinValue;

    private string CacheFolderPath => Path.Combine(AiOcrService.InstallFolderPath, "cache");


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public void Initialize(DispatcherQueue uiDispatcherQueue)
    {
        this.uiDispatcherQueue = uiDispatcherQueue;
        ProjectService.PropertyChanged += ProjectService_PropertyChanged;
        Watch(ProjectService.CurrentProject);
    }

    private void ProjectService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IProjectService.CurrentProject))
            Watch(ProjectService.CurrentProject);
    }

    private void Watch(ProjectBase? project)
    {
        if (ReferenceEquals(project, watchedProject))
            return;

        if (watchedProject != null)
            watchedProject.PropertyChanged -= Project_PropertyChanged;

        pendingExport?.Cancel();
        watchedProject = project;

        if (watchedProject != null)
            watchedProject.PropertyChanged += Project_PropertyChanged;
    }

    private void Project_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectBase.IsSaved) || sender is not ProjectBase project || !project.IsSaved)
            return;

        if (!AiOcrService.IsEnabled || !AiOcrService.IsInstalled || (!AiOcrService.OutputMarkdown && !AiOcrService.OutputPdf))
            return;

        // restart the countdown with every save
        pendingExport?.Cancel();
        CancellationTokenSource cancellation = new();
        pendingExport = cancellation;
        _ = Task.Run(() => RunExportAsync(project, cancellation.Token));
    }

    private async Task RunExportAsync(ProjectBase project, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(debounceDelay, cancellationToken);

            await exportSemaphore.WaitAsync(cancellationToken);
            try
            {
                await ExportAsync(project, cancellationToken);
            }
            finally
            {
                exportSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer save or another project
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "AI text recognition export failed");
            NotifyError(exc.Message);
        }
    }

    private async Task ExportAsync(ProjectBase project, CancellationToken cancellationToken)
    {
        List<SavedProjectFile>? files = await project.TryReadSavedFilesAsync(notifyIfNotSaved: false, waitForSave: false);
        if (files == null || !ReferenceEquals(project, ProjectService.CurrentProject))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        if (!await AiOcrService.EnsureReadyAsync(cancellationToken))
        {
            NotifyError(AiOcrService.ErrorDetails ?? AiOcrService.StatusText);
            return;
        }

        int analyzedPages = 0;
        foreach (SavedProjectFile file in files)
        {
            if (file.Folder == null)
            {
                LogService?.Log.Warning("Skipping AI output, the target folder is unknown");
                continue;
            }

            List<AiOcrPageImage> pages = await GetPageImagesAsync(file);
            List<AiOcrPageResult> results = [];
            foreach (AiOcrPageImage page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string hash = Convert.ToHexString(SHA256.HashData(page.Image));
                AiOcrPageResult? result = await TryLoadFromCacheAsync(hash);
                if (result == null)
                {
                    result = await AiOcrService.AnalyzeAsync(page.Image, cancellationToken);
                    await SaveToCacheAsync(hash, result);
                    analyzedPages++;
                }
                results.Add(result);
            }

            string baseName = Path.GetFileNameWithoutExtension(file.FileName);
            if (AiOcrService.OutputMarkdown)
            {
                StorageFile markdownFile = await file.Folder.CreateFileAsync(baseName + ".md", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(markdownFile, AiOcrDocumentWriter.CreateMarkdown(results));
            }
            if (AiOcrService.OutputPdf)
            {
                byte[] pdf = AiOcrDocumentWriter.CreateSearchablePdf(pages, results);
                StorageFile pdfFile = await file.Folder.CreateFileAsync(baseName + aiPdfSuffix + ".pdf", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(pdfFile, pdf);
            }
        }

        LogService?.Log.Information("AI outputs written ({Files} file(s), {Pages} newly analyzed page(s))", files.Count, analyzedPages);
        if (analyzedPages > 0)
        {
            Notify(Resources.Strings.Resources.AiOcrExportDoneHeading, Resources.Strings.Resources.AiOcrExportDoneBody, InfoBarSeverity.Success);
        }
    }

    #region Page images
    private static async Task<List<AiOcrPageImage>> GetPageImagesAsync(SavedProjectFile file)
    {
        string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => await RenderPdfPagesAsync(file.Content),
            ".jpg" or ".jpeg" or ".png" => [new AiOcrPageImage(file.Content, null, null)],
            _ => [new AiOcrPageImage(await ConvertToPngAsync(file.Content), null, null)],
        };
    }

    /// <summary>
    /// Renders every page at <see cref="pdfRenderDpi"/> as JPEG, used both for recognition and the AI PDF.
    /// </summary>
    private static async Task<List<AiOcrPageImage>> RenderPdfPagesAsync(byte[] pdf)
    {
        using InMemoryRandomAccessStream source = await ToStreamAsync(pdf);
        Windows.Data.Pdf.PdfDocument document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(source);

        List<AiOcrPageImage> pages = [];
        for (uint i = 0; i < document.PageCount; i++)
        {
            using Windows.Data.Pdf.PdfPage page = document.GetPage(i);

            // Size is in device independent pixels (1/96 inch)
            Windows.Foundation.Size size = page.Size;
            Windows.Data.Pdf.PdfPageRenderOptions options = new()
            {
                DestinationWidth = (uint)Math.Round(size.Width * pdfRenderDpi / 96),
                DestinationHeight = (uint)Math.Round(size.Height * pdfRenderDpi / 96),
                BitmapEncoderId = BitmapEncoder.JpegEncoderId,
            };

            using InMemoryRandomAccessStream target = new();
            await page.RenderToStreamAsync(target, options);
            pages.Add(new AiOcrPageImage(await ReadAllBytesAsync(target), size.Width * 72 / 96, size.Height * 72 / 96));
        }
        return pages;
    }

    private static async Task<byte[]> ConvertToPngAsync(byte[] image)
    {
        using InMemoryRandomAccessStream source = await ToStreamAsync(image);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(source);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        using InMemoryRandomAccessStream target = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, target);
        encoder.SetSoftwareBitmap(bitmap);
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();
        return await ReadAllBytesAsync(target);
    }

    private static async Task<InMemoryRandomAccessStream> ToStreamAsync(byte[] content)
    {
        InMemoryRandomAccessStream stream = new();
        using (DataWriter writer = new(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(content);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        stream.Seek(0);
        return stream;
    }

    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        using Stream netStream = stream.CloneStream().AsStreamForRead();
        using MemoryStream memoryStream = new();
        await netStream.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
    }
    #endregion

    #region Cache
    private async Task<AiOcrPageResult?> TryLoadFromCacheAsync(string hash)
    {
        string path = Path.Combine(CacheFolderPath, hash + ".json");
        if (!File.Exists(path))
            return null;

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AiOcrPageResult>(stream);
        }
        catch (Exception exc)
        {
            LogService?.Log.Warning(exc, "Ignoring unreadable AI text recognition cache entry");
            return null;
        }
    }

    private async Task SaveToCacheAsync(string hash, AiOcrPageResult result)
    {
        try
        {
            Directory.CreateDirectory(CacheFolderPath);
            await using FileStream stream = File.Create(Path.Combine(CacheFolderPath, hash + ".json"));
            await JsonSerializer.SerializeAsync(stream, result);
        }
        catch (Exception exc)
        {
            LogService?.Log.Warning(exc, "Failed to cache AI text recognition result");
        }
    }
    #endregion

    #region Notifications
    private void NotifyError(string? details)
    {
        if (DateTime.UtcNow - lastErrorNotification < errorNotificationInterval)
            return;
        lastErrorNotification = DateTime.UtcNow;

        string message = string.IsNullOrWhiteSpace(details)
            ? Resources.Strings.Resources.AiOcrExportFailedBody
            : $"{Resources.Strings.Resources.AiOcrExportFailedBody} {details}";
        Notify(Resources.Strings.Resources.AiOcrExportFailedHeading, message, InfoBarSeverity.Error);
    }

    private void Notify(string title, string message, InfoBarSeverity severity)
    {
        uiDispatcherQueue?.TryEnqueue(() =>
        {
            WeakReferenceMessenger.Default.Send(new ShowInAppNotificationMessage(new CommunityToolkit.WinUI.Behaviors.Notification
            {
                Title = title,
                Message = message,
                Severity = severity,
            }));
        });
    }
    #endregion
}
