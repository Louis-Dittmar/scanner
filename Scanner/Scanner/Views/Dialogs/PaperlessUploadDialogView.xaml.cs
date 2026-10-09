using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Scanner.Models.Paperless;
using Scanner.Services.Interfaces;
using Scanner.ViewModels;
using System;
using System.Collections.Generic;

namespace Scanner.Views.Dialogs;

public sealed partial class PaperlessUploadDialogView : ContentDialog
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Services
    public readonly IAccessibilityService AccessibilityService = Ioc.Default.GetRequiredService<IAccessibilityService>();
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    public PaperlessUploadDialogViewModel ViewModel { get; }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public PaperlessUploadDialogView(IReadOnlyList<PaperlessUploadFile> files, string suggestedTitle)
    {
        ViewModel = new PaperlessUploadDialogViewModel(files, suggestedTitle);

        this.InitializeComponent();
        LogService?.Log.Information("Dialog loaded");
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private async void ContentDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Failed to load Paperless data");
        }
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // keep the dialog open to show progress and the result
        args.Cancel = true;

        try
        {
            await ViewModel.UploadAsync();
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Paperless upload failed unexpectedly");
        }
    }

    private void ContentDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        ViewModel.Dispose();
    }
}
