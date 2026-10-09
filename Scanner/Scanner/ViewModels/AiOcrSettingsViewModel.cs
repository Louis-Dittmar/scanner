using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Scanner.Models.AiOcr;
using Scanner.Services.Interfaces;
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
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    #region Commands
    public AsyncRelayCommand InstallAsyncCommand { get; }
    public RelayCommand CancelInstallCommand { get; }
    public AsyncRelayCommand StartAsyncCommand { get; }
    public AsyncRelayCommand StopAsyncCommand { get; }
    public AsyncRelayCommand UninstallAsyncCommand { get; }
    public AsyncRelayCommand OpenFolderAsyncCommand { get; }
    #endregion

    private readonly DispatcherQueue? dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? installCancellation;

    public bool IsSupported => AiOcrService.IsSupported;
    public bool IsUnsupported => !AiOcrService.IsSupported;
    public bool IsInstalled => AiOcrService.IsInstalled;
    public bool CanInstall => IsSupported && !IsInstalled && !IsWorking;
    public bool IsInstalling => AiOcrService.State == AiOcrState.Installing;
    public bool IsWorking => AiOcrService.State is AiOcrState.Installing or AiOcrState.Starting or AiOcrState.DownloadingModel or AiOcrState.LoadingModel;
    public bool CanStart => IsInstalled && AiOcrService.State is AiOcrState.Stopped or AiOcrState.Error;
    public bool CanStop => IsInstalled && AiOcrService.State is AiOcrState.Ready or AiOcrState.Starting or AiOcrState.DownloadingModel or AiOcrState.LoadingModel;
    public bool CanUninstall => IsInstalled && !IsWorking;

    public string StatusText => AiOcrService.StatusText;
    public string? ErrorDetails => AiOcrService.ErrorDetails;
    public bool HasError => AiOcrService.State == AiOcrState.Error;
    public bool IsProgressIndeterminate => AiOcrService.Progress == null;
    public double ProgressValue => (AiOcrService.Progress ?? 0) * 100;
    public string InstallFolderPath => AiOcrService.InstallFolderPath;

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

    public bool OutputMarkdown
    {
        get => AiOcrService.OutputMarkdown;
        set { AiOcrService.OutputMarkdown = value; OnPropertyChanged(); }
    }

    public bool OutputPdf
    {
        get => AiOcrService.OutputPdf;
        set { AiOcrService.OutputPdf = value; OnPropertyChanged(); }
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


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public AiOcrSettingsViewModel()
    {
        InstallAsyncCommand = new AsyncRelayCommand(InstallAsync);
        CancelInstallCommand = new RelayCommand(() => installCancellation?.Cancel());
        StartAsyncCommand = new AsyncRelayCommand(() => AiOcrService.EnsureReadyAsync());
        StopAsyncCommand = new AsyncRelayCommand(AiOcrService.StopAsync);
        UninstallAsyncCommand = new AsyncRelayCommand(UninstallAsync);
        OpenFolderAsyncCommand = new AsyncRelayCommand(OpenFolderAsync);

        AiOcrService.PropertyChanged += AiOcrService_PropertyChanged;
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public void Dispose()
    {
        AiOcrService.PropertyChanged -= AiOcrService_PropertyChanged;
    }

    private void AiOcrService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // the service reports from background threads
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
        await AiOcrService.UninstallAsync();
    }

    private async Task OpenFolderAsync()
    {
        Directory.CreateDirectory(InstallFolderPath);
        await Windows.System.Launcher.LaunchFolderPathAsync(InstallFolderPath);
    }
}
