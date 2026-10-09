using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Scanner.Extensions;
using Scanner.Messages;
using Scanner.Services.Interfaces;
using Scanner.Services.Pipeline;
using Scanner.Views.Dialogs;
using System;
using System.Threading.Tasks;

namespace Scanner.Views;

public sealed partial class ShellView
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Asks on launch whether to set up the AI text recognition, once the window is shown and no other dialog is open
    /// (the upstream setup dialog comes first on the very first launch). Kept apart from the main ShellView code to
    /// make merging upstream changes easier.
    /// </summary>
    private void RegisterAiSetup()
    {
        WeakReferenceMessenger.Default.Register<ShowAiSetupDialogMessage>(this, (r, m) =>
        {
            _ = ShowAiSetupDialogAsync(waitForOtherDialogs: false);
        });

        Loaded += async (s, e) =>
        {
            IAiOcrService aiOcrService = Ioc.Default.GetRequiredService<IAiOcrService>();
            if (App.IsUiTestMode || !aiOcrService.IsSupported || aiOcrService.IsInstalled || aiOcrService.SetupPromptDismissed || AiSetup.IsInstalling)
                return;

            await Task.Delay(TimeSpan.FromSeconds(2));
            await ShowAiSetupDialogAsync(waitForOtherDialogs: true);
        };
    }

    private async Task ShowAiSetupDialogAsync(bool waitForOtherDialogs)
    {
        try
        {
            // wait for e.g. the first-run setup dialog to be closed
            for (int i = 0; waitForOtherDialogs && isDialogVisible && i < 600; i++)
                await Task.Delay(1000);

            if (isDialogVisible || XamlRoot == null)
                return;

            isDialogVisible = true;
            try
            {
                AiSetupDialogView dialog = new() { XamlRoot = XamlRoot };
                await dialog.ShowAsync();
            }
            finally
            {
                isDialogVisible = false;
            }
        }
        catch (Exception exc)
        {
            ViewModel.LogService?.Log.Error(exc, "Failed to show the AI setup dialog");
        }
    }
}
