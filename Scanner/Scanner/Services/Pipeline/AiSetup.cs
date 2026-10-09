using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Scanner.Messages;
using Scanner.Services.Interfaces;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUIEx;

namespace Scanner.Services.Pipeline;

/// <summary>
/// Installing the AI text recognition outside of the settings page, e.g. from the first-run dialog: runs in the
/// background, reports through in-app notifications and switches the recognition on once it's done.
/// </summary>
internal static class AiSetup
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Python with PyTorch (about 5 GB) plus the model (about 7 GB) and some room for the cache.
    /// </summary>
    public const long RequiredBytes = 14L * 1024 * 1024 * 1024;

    private static CancellationTokenSource? installation;

    public static bool IsInstalling => installation != null;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public static void StartInstallInBackground(DispatcherQueue uiDispatcherQueue)
    {
        if (installation != null)
            return;

        IAiOcrService aiOcrService = Ioc.Default.GetRequiredService<IAiOcrService>();
        ILogService? logService = Ioc.Default.GetService<ILogService>();
        CancellationTokenSource cancellation = new();
        installation = cancellation;

        Notify(uiDispatcherQueue, Resources.Strings.Resources.AiSetupStartedHeading, Resources.Strings.Resources.AiSetupStartedBody, InfoBarSeverity.Informational);
        _ = Task.Run(async () =>
        {
            try
            {
                await aiOcrService.InstallAsync(cancellation.Token);
                aiOcrService.IsEnabled = true;

                // loads (and on first use downloads) the model right away
                await aiOcrService.StartIfEnabledAsync();
                Notify(uiDispatcherQueue, Resources.Strings.Resources.AiSetupDoneHeading, Resources.Strings.Resources.AiSetupDoneBody, InfoBarSeverity.Success);
            }
            catch (OperationCanceledException)
            {
                logService?.Log.Information("AI text recognition installation cancelled");
            }
            catch (Exception exc)
            {
                logService?.Log.Error(exc, "AI text recognition installation failed");
                Notify(uiDispatcherQueue, Resources.Strings.Resources.AiOcrInstallFailed, aiOcrService.ErrorDetails ?? exc.Message, InfoBarSeverity.Error);
            }
            finally
            {
                installation = null;
            }
        });
    }

    public static void CancelInstall() => installation?.Cancel();

    /// <summary>
    /// Free space on the drive of <paramref name="folder"/>, <see langword="null"/> if unknown.
    /// </summary>
    public static long? GetFreeSpace(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            return root == null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string FormatGigabytes(long bytes) => $"{bytes / 1024d / 1024 / 1024:0.#} GB";

    public static async Task<string?> PickFolderAsync(Window window)
    {
        FolderPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, window.GetWindowHandle());
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static void Notify(DispatcherQueue uiDispatcherQueue, string title, string message, InfoBarSeverity severity)
    {
        uiDispatcherQueue.TryEnqueue(() =>
        {
            WeakReferenceMessenger.Default.Send(new ShowInAppNotificationMessage(new CommunityToolkit.WinUI.Behaviors.Notification
            {
                Title = title,
                Message = message,
                Severity = severity,
            }));
        });
    }
}
