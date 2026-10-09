
namespace Scanner.Tests;

public static class ScanOptions
{
    public const string ScannersComboBoxId = "ScannersComboBox";
    public const string AddDebugScannerButtonId = "AddDebugScannerButton";
}

public static class ScanActions
{
    public const string ScanButtonId = "ScanButton";
}

public static class DocumentWindow
{
    /// <summary>On a layout panel, which UI automation doesn't expose; find the window by <see cref="StatusTextId"/>.</summary>
    public const string RootId = "DocumentWindowRoot";
    public const string StatusTextId = "DocumentStatusText";
    public const string OutputPathId = "DocumentOutputPath";
    public const string OpenPdfButtonId = "DocumentOpenPdfButton";
}

public static class ProjectMenu
{
    public const string CreateAiDocumentId = "CreateAiDocumentMenuItem";
}
