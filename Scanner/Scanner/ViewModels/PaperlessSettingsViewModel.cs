using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Scanner.Models.Paperless;
using Scanner.Services.Interfaces;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Scanner.ViewModels;

public partial class PaperlessSettingsViewModel : ObservableObject, IDisposable
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Services
    public readonly IPaperlessService PaperlessService = Ioc.Default.GetRequiredService<IPaperlessService>();
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    #region Commands
    public AsyncRelayCommand TestConnectionAsyncCommand { get; }
    public AsyncRelayCommand SaveAsyncCommand { get; }
    public RelayCommand RemoveCommand { get; }
    #endregion

    [ObservableProperty]
    private string serverUrl;

    /// <summary>
    /// A newly entered token. Empty means "keep the stored one".
    /// </summary>
    [ObservableProperty]
    private string token = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? statusMessage;

    [ObservableProperty]
    private InfoBarSeverity statusSeverity = InfoBarSeverity.Informational;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public bool IsConfigured => PaperlessService.IsConfigured;

    public string TokenPlaceholder => PaperlessService.HasToken
        ? Resources.Strings.Resources.PaperlessTokenStoredPlaceholder
        : Resources.Strings.Resources.PaperlessTokenPlaceholder;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public PaperlessSettingsViewModel()
    {
        serverUrl = PaperlessService.ServerUrl ?? "";

        TestConnectionAsyncCommand = new AsyncRelayCommand(TestConnectionAsync);
        SaveAsyncCommand = new AsyncRelayCommand(SaveAsync);
        RemoveCommand = new RelayCommand(Remove);

        PaperlessService.PropertyChanged += PaperlessService_PropertyChanged;
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public void Dispose()
    {
        PaperlessService.PropertyChanged -= PaperlessService_PropertyChanged;
    }

    private void PaperlessService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPaperlessService.HasToken):
                OnPropertyChanged(nameof(TokenPlaceholder));
                break;
            case nameof(IPaperlessService.IsConfigured):
                OnPropertyChanged(nameof(IsConfigured));
                break;
        }
    }

    private async Task<PaperlessConnectionInfo> RunConnectionTestAsync()
    {
        IsBusy = true;
        StatusMessage = null;

        try
        {
            return await PaperlessService.TestConnectionAsync(ServerUrl, Token);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestConnectionAsync()
    {
        LogService?.Log.Information("Testing Paperless connection");
        PaperlessConnectionInfo result = await RunConnectionTestAsync();
        ShowResult(result, Resources.Strings.Resources.PaperlessConnectionSuccess);
    }

    /// <summary>
    /// Only stores connections that actually work, so the upload dialog never runs into a broken configuration.
    /// </summary>
    private async Task SaveAsync()
    {
        LogService?.Log.Information("Saving Paperless connection");
        PaperlessConnectionInfo result = await RunConnectionTestAsync();
        if (!result.IsSuccess)
        {
            ShowResult(result, "");
            return;
        }

        try
        {
            PaperlessService.SaveConfiguration(ServerUrl, Token);
            ServerUrl = PaperlessService.ServerUrl ?? ServerUrl;    // show the normalized address
            Token = "";
            ShowResult(result, Resources.Strings.Resources.PaperlessConfigurationSaved);
        }
        catch (PaperlessException exc)
        {
            ShowResult(new PaperlessConnectionInfo(exc.Error, null), "");
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Failed to save Paperless connection");
            ShowResult(new PaperlessConnectionInfo(PaperlessConnectionError.Unknown, null), "");
        }
    }

    private void Remove()
    {
        PaperlessService.RemoveConfiguration();
        ServerUrl = "";
        Token = "";
        StatusSeverity = InfoBarSeverity.Informational;
        StatusMessage = Resources.Strings.Resources.PaperlessConfigurationRemoved;
    }

    private void ShowResult(PaperlessConnectionInfo result, string successText)
    {
        if (result.IsSuccess)
        {
            StatusSeverity = InfoBarSeverity.Success;
            StatusMessage = string.IsNullOrEmpty(result.ServerVersion)
                ? successText
                : $"{successText} (Paperless-ngx {result.ServerVersion})";
        }
        else
        {
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = PaperlessTexts.GetErrorText(result.Error);
        }
    }
}
