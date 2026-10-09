using Scanner.Models.Paperless;
using System.Collections.Generic;

namespace Scanner.Messages;

internal class ShowPaperlessUploadDialogMessage
{
    public readonly IReadOnlyList<PaperlessUploadFile> Files;

    /// <summary>
    /// Used as the document title if the user doesn't enter one.
    /// </summary>
    public readonly string SuggestedTitle;

    public ShowPaperlessUploadDialogMessage(IReadOnlyList<PaperlessUploadFile> files, string suggestedTitle)
    {
        Files = files;
        SuggestedTitle = suggestedTitle;
    }
}
