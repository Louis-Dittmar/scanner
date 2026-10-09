using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Controls;
using Scanner.Messages;
using Scanner.Models.Paperless;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Scanner.Models;

/// <summary>
/// A saved target file of a project, read into memory so it can be processed without holding project locks.
/// </summary>
public record SavedProjectFile(string FileName, byte[] Content, string ContentType, StorageFolder? Folder);

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
        List<SavedProjectFile>? files = await TryReadSavedFilesAsync(notifyIfNotSaved: true, waitForSave: true);
        return files?.Select(x => new PaperlessUploadFile(x.FileName, x.Content, x.ContentType)).ToList();
    }

    /// <summary>
    /// Reads the saved target file(s) of this project into memory.
    /// </summary>
    /// <param name="notifyIfNotSaved">Show the "not saved" notification if there is nothing up-to-date to read.</param>
    /// <param name="waitForSave">Show the "save in progress" dialog and wait for a running save; background callers
    /// pass <see langword="false"/> and simply wait for the save lock instead.</param>
    /// <returns>The files, or <see langword="null"/> if the project isn't saved.</returns>
    public async Task<List<SavedProjectFile>?> TryReadSavedFilesAsync(bool notifyIfNotSaved, bool waitForSave)
    {
        // wait for save processes to end
        if (waitForSave && LatestSaveProcess != null && !LatestSaveProcess.Task.IsCompleted)
        {
            await Messenger.Send(new ShowSaveInProgressDialogMessage()).Response;
        }
        await saveSemaphore.WaitAsync();
        await projectObjectSemaphore.WaitAsync();

        try
        {
            List<(StorageFile File, StorageFolder? Folder)>? files = IsSaved ? GetSavedTargetFiles() : null;
            if (files == null || files.Count == 0)
            {
                if (notifyIfNotSaved)
                {
                    Messenger.Send(new ShowInAppNotificationMessage(new CommunityToolkit.WinUI.Behaviors.Notification
                    {
                        Title = Resources.Strings.Resources.ProjectNotSavedHeading,
                        Message = Resources.Strings.Resources.ProjectNotSavedBody,
                        Severity = InfoBarSeverity.Error
                    }));
                }
                return null;
            }

            // target files are opened with AllowOnlyReaders while the project is open, so reading is fine
            List<SavedProjectFile> result = [];
            foreach ((StorageFile file, StorageFolder? folder) in files)
            {
                result.Add(new SavedProjectFile(file.Name, await ReadAllBytesAsync(file), GetContentType(file), folder));
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
    /// The files this project has been saved to and the folders they're in, only called while holding the project locks.
    /// </summary>
    protected abstract List<(StorageFile File, StorageFolder? Folder)>? GetSavedTargetFiles();

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
