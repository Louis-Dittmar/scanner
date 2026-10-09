using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Scanner.Core.Pdf;
using Scanner.Models.AiOcr;
using Scanner.Services.Interfaces;
using Scanner.Services.Pipeline;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Scanner.ViewModels;

public partial class AiOcrSettingsViewModel : ObservableObject, IDisposable
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Services
    public readonly IAiOcrService AiOcrService = Ioc.Default.GetRequiredService<IAiOcrService>();
    public readonly IDocumentPipelineService PipelineService = Ioc.Default.GetRequiredService<IDocumentPipelineService>();
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    #region Commands
    public AsyncRelayCommand InstallAsyncCommand { get; }
    public RelayCommand CancelInstallCommand { get; }
    public AsyncRelayCommand StartAsyncCommand { get; }
    public AsyncRelayCommand StopAsyncCommand { get; }
    public AsyncRelayCommand UninstallAsyncCommand { get; }
    public AsyncRelayCommand OpenFolderAsyncCommand { get; }
    public AsyncRelayCommand OpenOutputFolderAsyncCommand { get; }
    public RelayCommand ResetOutputFolderCommand { get; }
    public RelayCommand ResetInstallLocationCommand { get; }
    #endregion

    private readonly DispatcherQueue? dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? installCancellation;

    public bool IsSupported => AiOcrService.IsSupported;
    public bool IsUnsupported => !AiOcrService.IsSupported;
    public bool IsInstalled => AiOcrService.IsInstalled;
    public bool CanInstall => IsSupported && !IsInstalled && !IsWorking && !AiSetup.IsInstalling;
    public bool IsInstalling => AiOcrService.State == AiOcrState.Installing;
    public bool IsWorking => AiOcrService.State is AiOcrState.Installing or AiOcrState.Starting or AiOcrState.DownloadingModel or AiOcrState.LoadingModel;
    public bool CanStart => IsInstalled && AiOcrService.State is AiOcrState.Stopped or AiOcrState.Error;
    public bool CanStop => IsInstalled && AiOcrService.State is AiOcrState.Ready or AiOcrState.Starting or AiOcrState.DownloadingModel or AiOcrState.LoadingModel;
    public bool CanUninstall => IsInstalled && !IsWorking;
    public bool CanChangeInstallLocation => !IsInstalled && !IsWorking && !AiSetup.IsInstalling;

    public string StatusText => AiOcrService.StatusText;
    public string? ErrorDetails => AiOcrService.ErrorDetails;
    public bool HasError => AiOcrService.State == AiOcrState.Error;
    public bool IsProgressIndeterminate => AiOcrService.Progress == null;
    public double ProgressValue => (AiOcrService.Progress ?? 0) * 100;
    public string InstallFolderPath => AiOcrService.InstallFolderPath;
    public bool IsCustomInstallLocation => !AiOcrService.IsDefaultInstallLocation;

    public string StorageDescription => AiOcrService.IsDefaultInstallLocation
        ? $"{InstallFolderPath}\n{Resources.Strings.Resources.AiSetupRemovalDefault}"
        : $"{InstallFolderPath}\n{Resources.Strings.Resources.AiSetupRemovalCustom}";

    public bool IsEnabled
    {
        get => AiOcrService.IsEnabled;
        set
        {
            if (AiOcrService.IsEnabled == value)
                return;

            AiOcrService.IsEnabled = value;
            OnPropertyChanged();
            _ = value ? AiOcrService.StartIfEnabledAsync() : AiOcrService.StopAsync();
        }
    }

    /// <summary>
    /// As double for NumberBox.
    /// </summary>
    public double LingerMinutes
    {
        get => AiOcrService.LingerMinutes;
        set
        {
            if (double.IsNaN(value))
                return;
            AiOcrService.LingerMinutes = (int)Math.Round(value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// As double for NumberBox. Takes effect the next time the model is loaded.
    /// </summary>
    public double IdleUnloadMinutes
    {
        get => AiOcrService.IdleUnloadMinutes;
        set
        {
            if (double.IsNaN(value))
                return;
            AiOcrService.IdleUnloadMinutes = (int)Math.Round(value);
            OnPropertyChanged();
        }
    }

    #region Document settings
    public bool AutoProcessAfterScan
    {
        get => PipelineService.AutoProcessAfterScan;
        set { PipelineService.AutoProcessAfterScan = value; OnPropertyChanged(); }
    }

    public bool WhiteCorrection
    {
        get => PipelineService.WhiteCorrection;
        set { PipelineService.WhiteCorrection = value; OnPropertyChanged(); }
    }

    public bool CropToDocument
    {
        get => PipelineService.CropToDocument;
        set { PipelineService.CropToDocument = value; OnPropertyChanged(); }
    }

    public int PdfModeIndex
    {
        get => PipelineService.PdfMode == PdfOutputMode.Reconstructed ? 0 : 1;
        set
        {
            if (value < 0)
                return;
            PipelineService.PdfMode = value == 0 ? PdfOutputMode.Reconstructed : PdfOutputMode.ScanWithTextLayer;
            OnPropertyChanged();
        }
    }

    public bool WriteMarkdown
    {
        get => PipelineService.WriteMarkdown;
        set { PipelineService.WriteMarkdown = value; OnPropertyChanged(); }
    }

    public string OutputFolderPath => PipelineService.OutputFolderPath;
    public bool IsCustomOutputFolder => PipelineService.OutputFolderPath != PipelineService.DefaultOutputFolderPath;
    #endregion


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public AiOcrSettingsViewModel()
    {
        InstallAsyncCommand = new AsyncRelayCommand(InstallAsync);
        CancelInstallCommand = new RelayCommand(() =>
        {
            installCancellation?.Cancel();
            AiSetup.CancelInstall();
        });
        StartAsyncCommand = new AsyncRelayCommand(() => AiOcrService.EnsureReadyAsync());
        StopAsyncCommand = new AsyncRelayCommand(AiOcrService.StopAsync);
        UninstallAsyncCommand = new AsyncRelayCommand(UninstallAsync);
        OpenFolderAsyncCommand = new AsyncRelayCommand(OpenFolderAsync);
        OpenOutputFolderAsyncCommand = new AsyncRelayCommand(OpenOutputFolderAsync);
        ResetOutputFolderCommand = new RelayCommand(() => SetOutputFolder(PipelineService.DefaultOutputFolderPath));
        ResetInstallLocationCommand = new RelayCommand(() => SetInstallLocation(null));

        AiOcrService.PropertyChanged += Service_PropertyChanged;
        PipelineService.PropertyChanged += Service_PropertyChanged;
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public void Dispose()
    {
        AiOcrService.PropertyChanged -= Service_PropertyChanged;
        PipelineService.PropertyChanged -= Service_PropertyChanged;
    }

    private void Service_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // the services report from background threads
        if (dispatcherQueue == null || dispatcherQueue.HasThreadAccess)
            RefreshAll();
        else
            dispatcherQueue.TryEnqueue(RefreshAll);
    }

    private void RefreshAll()
    {
        // empty name: every binding re-reads its value
        OnPropertyChanged(string.Empty);
    }

    public void SetOutputFolder(string folder)
    {
        PipelineService.OutputFolderPath = folder;
        LogService?.Log.Information("Document output folder changed, default: {IsDefault}", !IsCustomOutputFolder);
        RefreshAll();
    }

    public void SetInstallLocation(string? parentFolder)
    {
        try
        {
            AiOcrService.SetInstallLocation(parentFolder);
        }
        catch (InvalidOperationException exc)
        {
            LogService?.Log.Warning(exc, "Install location can't be changed now");
        }
        RefreshAll();
    }

    private async Task InstallAsync()
    {
        installCancellation = new CancellationTokenSource();
        try
        {
            await AiOcrService.InstallAsync(installCancellation.Token);

            // switch it on right away, that's why it was installed
            IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            LogService?.Log.Information("AI text recognition installation cancelled");
        }
        catch (Exception)
        {
            // state and details are shown by the service
        }
        finally
        {
            installCancellation = null;
        }
    }

    private async Task UninstallAsync()
    {
        AiOcrService.IsEnabled = false;
        OnPropertyChanged(nameof(IsEnabled));
        try
        {
            await AiOcrService.UninstallAsync();
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Removing the AI text recognition failed");
        }
    }

    private async Task OpenFolderAsync()
    {
        Directory.CreateDirectory(InstallFolderPath);
        await Windows.System.Launcher.LaunchFolderPathAsync(InstallFolderPath);
    }

    private async Task OpenOutputFolderAsync()
    {
        Directory.CreateDirectory(OutputFolderPath);
        await Windows.System.Launcher.LaunchFolderPathAsync(OutputFolderPath);
    }
}
