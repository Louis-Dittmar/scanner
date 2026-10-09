using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Controls;
using Scanner.Messages;
using Scanner.Models.Paperless;
using Scanner.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Scanner.ViewModels;

partial class ProjectViewModel
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public readonly IPaperlessService PaperlessService = Ioc.Default.GetRequiredService<IPaperlessService>();

    public AsyncRelayCommand SendToPaperlessAsyncCommand => new AsyncRelayCommand(SendToPaperlessAsync);


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private async Task SendToPaperlessAsync()
    {
        if (CurrentProject == null)
            return;

        // point to the settings if there is no connection yet
        if (!PaperlessService.IsConfigured)
        {
            LogService?.Log.Information("Paperless not configured, opening settings");
            Messenger.Send(new ShowSettingsMessage(new SettingsViewModelIntent(SettingsPageType.Paperless)));
            return;
        }

        List<PaperlessUploadFile>? files;
        try
        {
            files = await CurrentProject.TryReadSavedFilesForUploadAsync();
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Failed to read project files for Paperless upload");
            Messenger.Send(new ShowInAppNotificationMessage(new CommunityToolkit.WinUI.Behaviors.Notification
            {
                Title = Resources.Strings.Resources.PaperlessUploadFailedHeading,
                Message = Resources.Strings.Resources.PaperlessErrorReadingFiles,
                Severity = InfoBarSeverity.Error
            }));
            return;
        }

        if (files == null)
            return;     // not saved, the project already informed the user

        LogService?.Log.Information("Opening Paperless upload dialog for {Count} file(s)", files.Count);
        Messenger.Send(new ShowPaperlessUploadDialogMessage(files, FileName));
    }
}
