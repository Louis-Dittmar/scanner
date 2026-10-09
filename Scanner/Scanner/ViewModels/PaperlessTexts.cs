using Scanner.Models.Paperless;

namespace Scanner.ViewModels;

/// <summary>
/// Localized texts shared by the Paperless-ngx settings page and upload dialog.
/// </summary>
internal static class PaperlessTexts
{
    public static string GetErrorText(PaperlessConnectionError error)
    {
        return error switch
        {
            PaperlessConnectionError.MissingInput => Resources.Strings.Resources.PaperlessErrorMissingInput,
            PaperlessConnectionError.InvalidUrl => Resources.Strings.Resources.PaperlessErrorInvalidUrl,
            PaperlessConnectionError.Unreachable => Resources.Strings.Resources.PaperlessErrorUnreachable,
            PaperlessConnectionError.Timeout => Resources.Strings.Resources.PaperlessErrorTimeout,
            PaperlessConnectionError.Unauthorized => Resources.Strings.Resources.PaperlessErrorUnauthorized,
            PaperlessConnectionError.NotPaperless => Resources.Strings.Resources.PaperlessErrorNotPaperless,
            _ => Resources.Strings.Resources.PaperlessErrorUnknown,
        };
    }
}
