using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using ScannerTests.Helpers;

namespace ScannerTests;

/// <summary>
/// End-to-end tests against the installed app, started with "--ui-test": no first-run dialogs, and the AI model is
/// replaced by a stand-in, so the whole document pipeline runs without a GPU.
/// </summary>
[TestClass]
public sealed class GeneralTests
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public TestContext TestContext { get; set; } = null!;

    private static readonly TimeSpan startTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan pipelineTimeout = TimeSpan.FromMinutes(3);

    private Application? application;
    private UIA3Automation? automation;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    [ClassInitialize]
    public static void InitializeTests(TestContext testContext)
    {
        Retry.DefaultInterval = TimeSpan.FromMilliseconds(250);
        Retry.DefaultTimeout = TimeSpan.FromSeconds(10);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (TestContext.CurrentTestOutcome != UnitTestOutcome.Passed)
            SaveScreenshot("failure");

        application?.Close(true);
        application?.Dispose();
        automation?.Dispose();
        Thread.Sleep(2000);     // let the single-instance app go away before the next test starts it again
    }

    private Window LaunchApp()
    {
        application = Application.LaunchStoreApp(Constants.APP_USER_MODEL_ID, Constants.UI_TEST_ARGUMENT);
        automation = new UIA3Automation();
        Window mainWindow = application.GetMainWindow(automation, startTimeout)
            ?? throw new AssertFailedException("The main window didn't appear");
        return mainWindow;
    }

    private static T WaitFor<T>(Func<T?> find, TimeSpan timeout, string what) where T : class
    {
        return Retry.WhileNull(find, timeout, TimeSpan.FromMilliseconds(250), throwOnTimeout: false).Result
            ?? throw new AssertFailedException($"Timed out waiting for {what}");
    }

    private void SaveScreenshot(string name)
    {
        try
        {
            string folder = Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), "Screenshots");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"{TestContext.TestName}-{name}.png");
            Capture.Screen().ToFile(path);
            TestContext.AddResultFile(path);
            TestContext.WriteLine($"Screenshot: {path}");
        }
        catch (Exception exc)
        {
            TestContext.WriteLine($"Screenshot failed: {exc.Message}");
        }
    }

    [TestMethod]
    public void AppStarts()
    {
        Window mainWindow = LaunchApp();
        ConditionFactory cf = mainWindow.ConditionFactory;

        WaitFor(() => mainWindow.FindFirstDescendant(cf.ByAutomationId(Scanner.Tests.ScanActions.ScanButtonId)), startTimeout, "the scan button");
        SaveScreenshot("started");
    }

    [TestMethod]
    public void ScanIsTurnedIntoSelectablePdf()
    {
        Window mainWindow = LaunchApp();
        ConditionFactory cf = mainWindow.ConditionFactory;

        // scan a test image with the debug scanner
        WaitFor(() => mainWindow.FindFirstDescendant(cf.ByAutomationId(Scanner.Tests.ScanOptions.ScannersComboBoxId)), startTimeout, "the scanner list")
            .AsComboBox().RightClick();
        WaitFor(() => mainWindow.FindFirstDescendant(cf.ByAutomationId(Scanner.Tests.ScanOptions.AddDebugScannerButtonId)), Retry.DefaultTimeout, "'Add debug scanner'")
            .AsButton().Click();
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESC);
        WaitFor(() => mainWindow.FindFirstDescendant(cf.ByAutomationId(Scanner.Tests.ScanActions.ScanButtonId)), Retry.DefaultTimeout, "the scan button")
            .AsButton().Click();

        Window filePickerWindow = WaitFor(() => mainWindow.ModalWindows.FirstOrDefault(), TimeSpan.FromSeconds(30), "the file picker");
        // a full path in the file name box works regardless of the folder the picker shows
        FileOpenPickerHelper.SetFiles(cf, filePickerWindow, Path.Combine(Constants.TestImagesFolder, "Document Portrait 1.png"));
        FileOpenPickerHelper.ConfirmSelection(cf, filePickerWindow);

        // the document window opens by itself and runs white correction, recognition and PDF creation
        Window documentWindow = WaitFor(() => application!.GetAllTopLevelWindows(automation!)
            .FirstOrDefault(w => w.FindFirstDescendant(cf.ByAutomationId(Scanner.Tests.DocumentWindow.RootId)) != null),
            TimeSpan.FromMinutes(1), "the document window");
        SaveScreenshot("document-window");

        AutomationElement outputInfo = WaitFor(() =>
        {
            AutomationElement? infoBar = documentWindow.FindFirstDescendant(cf.ByAutomationId(Scanner.Tests.DocumentWindow.OutputPathId));
            return infoBar?.FindAllDescendants(cf.ByControlType(ControlType.Text))
                .FirstOrDefault(t => t.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        }, pipelineTimeout, "the saved PDF");
        SaveScreenshot("done");

        string pdfPath = outputInfo.Name;
        TestContext.WriteLine($"PDF: {pdfPath}");
        Assert.IsTrue(File.Exists(pdfPath), $"{pdfPath} doesn't exist");
        byte[] header = File.ReadAllBytes(pdfPath)[..5];
        Assert.AreEqual("%PDF-", System.Text.Encoding.ASCII.GetString(header));
        StringAssert.Contains(File.ReadAllText(pdfPath, System.Text.Encoding.Latin1), "/Font", "the PDF must contain real text");
    }
}
