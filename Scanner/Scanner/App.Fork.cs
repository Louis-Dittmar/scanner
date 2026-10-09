using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Scanner.AppWindows;
using Scanner.Messages;
using Scanner.Services.Interfaces;
using System;
using System.Linq;

namespace Scanner;

/// <summary>
/// Startup of features added by this fork, kept apart from App.xaml.cs to make merging upstream changes easier.
/// </summary>
public partial class App
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Launch argument of the UI tests (ScannerTests): no first-run dialogs, and a stand-in for the AI model so the
    /// document pipeline also runs on machines without a GPU.
    /// </summary>
    public const string UiTestArgument = "--ui-test";

    public static bool IsUiTestMode { get; private set; }

    public DocumentWindow? DocumentWindow;

    /// <summary>
    /// Messenger recipient for the fork's registrations; App itself already receives some of the same messages.
    /// </summary>
    private readonly object forkMessageRecipient = new();


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Evaluates the launch arguments, before any service or window is created.
    /// </summary>
    public static void ReadForkLaunchArguments(string? arguments)
    {
        if (arguments?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(UiTestArgument) == true)
            IsUiTestMode = true;
    }

    private void InitializeForkFeatures()
    {
        try
        {
            Ioc.Default.GetRequiredService<IDocumentPipelineService>().Initialize(MainDispatcherQueue);

            WeakReferenceMessenger.Default.Register<ShowDocumentWindowMessage>(forkMessageRecipient, (r, m) =>
            {
                MainDispatcherQueue.TryEnqueue(ShowDocumentWindow);
            });
            WeakReferenceMessenger.Default.Register<MainWindowClosingMessage>(forkMessageRecipient, (r, m) =>
            {
                DocumentWindow?.Close();

                // the model is only loaded while the app runs
                Ioc.Default.GetService<IAiOcrService>()?.ShutdownForAppExit();
            });

            // load the model with the app, so it's ready by the time the first scan is saved
            if (!IsUiTestMode)
                _ = Ioc.Default.GetRequiredService<IAiOcrService>().StartIfEnabledAsync();
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Failed to initialize fork features");
        }
    }

    public void ShowDocumentWindow()
    {
        if (DocumentWindow == null)
        {
            DocumentWindow = new DocumentWindow();
            DocumentWindow.Closed += DocumentWindow_Closed;
        }
        DocumentWindow.Activate();
    }

    private void DocumentWindow_Closed(object sender, WindowEventArgs args)
    {
        DocumentWindow = null;
    }
}
