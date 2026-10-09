namespace ScannerTests;

public static class Constants
{
    public const string APP_USER_MODEL_ID = "LouisDittmar.ScannerPaperless_jdcc9wjam9c0t!App";

    /// <summary>
    /// Makes the app skip first-run dialogs and use a stand-in for the AI model (see App.Fork.cs).
    /// </summary>
    public const string UI_TEST_ARGUMENT = "--ui-test";

    /// <summary>
    /// The images the debug scanner "scans": SCANNER_TEST_IMAGES, or the folder in this repository.
    /// </summary>
    public static string TestImagesFolder
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("SCANNER_TEST_IMAGES");
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            {
                string candidate = Path.Combine(folder.FullName, "Scanner", "Resources", "Test Images");
                if (Directory.Exists(candidate))
                    return candidate;
            }
            throw new DirectoryNotFoundException("Test images not found, set SCANNER_TEST_IMAGES");
        }
    }
}
