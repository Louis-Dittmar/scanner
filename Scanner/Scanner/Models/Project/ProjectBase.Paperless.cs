using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Controls;
using Scanner.Messages;
using Scanner.Models.Paperless;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Scanner.Models;

public abstract partial class ProjectBase
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Reads the saved target file(s) of this project into memory for uploading to Paperless-ngx.
    /// Waits for running saves and shows the "not saved" notification if there is nothing up-to-date to send.
    /// </summary>
    /// <returns>The files, or <see langword="null"/> if the project isn't saved.</returns>
    public async Task<List<PaperlessUploadFile>?> TryReadSavedFilesForUploadAsync()
    {
        // wait for save processes to end
        if (LatestSaveProcess != null && !LatestSaveProcess.Task.IsCompleted)
        {
            await Messenger.Send(new ShowSaveInProgressDialogMessage()).Response;
        }
        await saveSemaphore.WaitAsync();
        await projectObjectSemaphore.WaitAsync();

        try
        {
            List<StorageFile>? files = IsSaved ? GetSavedTargetFiles() : null;
            if (files == null || files.Count == 0)
            {
                Messenger.Send(new ShowInAppNotificationMessage(new CommunityToolkit.WinUI.Behaviors.Notification
                {
                    Title = Resources.Strings.Resources.ProjectNotSavedHeading,
                    Message = Resources.Strings.Resources.ProjectNotSavedBody,
                    Severity = InfoBarSeverity.Error
                }));
                return null;
            }

            // target files are opened with AllowOnlyReaders while the project is open, so reading is fine
            List<PaperlessUploadFile> result = [];
            foreach (StorageFile file in files)
            {
                result.Add(new PaperlessUploadFile(file.Name, await ReadAllBytesAsync(file), GetContentType(file)));
            }
            return result;
        }
        finally
        {
            projectObjectSemaphore.Release();
            saveSemaphore.Release();
        }
    }

    /// <summary>
    /// The files this project has been saved to, only called while holding the project locks.
    /// </summary>
    protected abstract List<StorageFile>? GetSavedTargetFiles();

    private static async Task<byte[]> ReadAllBytesAsync(StorageFile file)
    {
        using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
        using Stream netStream = stream.AsStreamForRead();
        using MemoryStream memoryStream = new();
        await netStream.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
    }

    private static string GetContentType(StorageFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.ContentType))
            return file.ContentType;

        return Path.GetExtension(file.Name).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".tif" or ".tiff" => "image/tiff",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
    }
}
