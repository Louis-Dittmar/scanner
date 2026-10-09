using CommunityToolkit.Mvvm.DependencyInjection;
using Scanner.Services.Interfaces;
using System;

namespace Scanner;

/// <summary>
/// Startup of features added by this fork, kept apart from App.xaml.cs to make merging upstream changes easier.
/// </summary>
public partial class App
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private void InitializeForkFeatures()
    {
        try
        {
            Ioc.Default.GetRequiredService<IAiOcrExportService>().Initialize(MainDispatcherQueue);

            // the AI text recognition starts with the app, so the model is ready by the time the first scan is saved
            _ = Ioc.Default.GetRequiredService<IAiOcrService>().StartIfEnabledAsync();
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Failed to initialize fork features");
        }
    }
}
