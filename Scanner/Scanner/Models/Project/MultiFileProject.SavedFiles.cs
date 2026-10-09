using Scanner.Models.Interfaces;
using System.Collections.Generic;
using Windows.Storage;

namespace Scanner.Models;

public partial class MultiFileProject
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    protected override List<(StorageFile File, StorageFolder? Folder)>? GetSavedTargetFiles()
    {
        List<(StorageFile File, StorageFolder? Folder)> files = [];
        foreach (IProjectPage page in Pages)
        {
            if (page is not ImagePage imagePage || imagePage.TargetFile == null)
                return null;    // at least one page has no saved file yet

            files.Add((imagePage.TargetFile.File, imagePage.TargetFolder));
        }
        return files;
    }
}
