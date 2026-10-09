using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Scanner.Services.Interfaces;
using Scanner.ViewModels;
using System;
using System.ComponentModel;
using System.IO;

namespace Scanner.Views;

public sealed partial class DocumentProcessingView : Page
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public DocumentProcessingViewModel ViewModel { get; } = new();

    private DocumentJob? observedJob;
    private string? shownPdfPath;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public DocumentProcessingView()
    {
        this.InitializeComponent();
        Ioc.Default.GetService<ILogService>()?.Log.Information("View loaded");

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ObserveJob();
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (observedJob != null)
            observedJob.PropertyChanged -= Job_PropertyChanged;
        ViewModel.Dispose();
        PdfViewer.Close();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentProcessingViewModel.Job))
            ObserveJob();
    }

    private void ObserveJob()
    {
        if (observedJob != null)
            observedJob.PropertyChanged -= Job_PropertyChanged;
        observedJob = ViewModel.Job;
        if (observedJob != null)
            observedJob.PropertyChanged += Job_PropertyChanged;
        ShowPdf();
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // the PDF may be written again under the same name (other PDF mode), so reload on every completion
        if (e.PropertyName is nameof(DocumentJob.State) or nameof(DocumentJob.OutputPdfPath))
            ShowPdf();
    }

    /// <summary>
    /// Shows the created PDF in the built-in viewer of WebView2, where its text can be selected and searched.
    /// </summary>
    private async void ShowPdf()
    {
        try
        {
            string? path = observedJob is { IsDone: true } job ? job.OutputPdfPath : null;
            if (path == null || !File.Exists(path))
            {
                shownPdfPath = null;
                return;
            }

            await PdfViewer.EnsureCoreWebView2Async();
            Uri uri = new(path);
            if (shownPdfPath == path)
                PdfViewer.CoreWebView2.Reload();
            else
                PdfViewer.Source = uri;
            shownPdfPath = path;
        }
        catch (Exception exc)
        {
            // without the WebView2 runtime the PDF can still be opened in the default viewer
            Ioc.Default.GetService<ILogService>()?.Log.Warning(exc, "Failed to show the PDF preview");
        }
    }
}
