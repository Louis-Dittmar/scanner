using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Controls;
using Scanner.Extensions;
using Scanner.Messages;
using Scanner.Views.Dialogs;

namespace Scanner.Views;

public sealed partial class ShellView
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Kept apart from the main ShellView code to make merging upstream changes easier.
    /// </summary>
    private void RegisterPaperlessMessages()
    {
        WeakReferenceMessenger.Default.Register<ShowPaperlessUploadDialogMessage>(this, (r, m) =>
        {
            ShowPaperlessUploadDialog(m);
        });
    }

    private void ShowPaperlessUploadDialog(ShowPaperlessUploadDialogMessage message)
    {
        this.RunOnUIThread(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, async () =>
        {
            // return if dialog is already visible
            if (isDialogVisible)
            {
                return;
            }

            isDialogVisible = true;

            try
            {
                PaperlessUploadDialogView dialog = new(message.Files, message.SuggestedTitle);
                dialog.XamlRoot = this.XamlRoot;
                await dialog.ShowAsync();
            }
            finally
            {
                isDialogVisible = false;
            }
        });
    }
}
