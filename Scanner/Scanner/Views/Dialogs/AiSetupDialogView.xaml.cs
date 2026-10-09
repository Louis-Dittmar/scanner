using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Scanner.Services.Interfaces;
using Scanner.Services.Pipeline;
using Scanner.ViewModels;
using System;

namespace Scanner.Views.Dialogs;

public sealed partial class AiSetupDialogView : ContentDialog
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();

    public AiSetupDialogViewModel ViewModel { get; } = new();


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public AiSetupDialogView()
    {
        this.InitializeComponent();
        LogService?.Log.Information("Dialog loaded");
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            ViewModel.Install(DispatcherQueue);
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Failed to start the AI text recognition setup");
        }
    }

    private void ContentDialog_CloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ViewModel.Postpone();
    }

    private async void ButtonChangeLocation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = await AiSetup.PickFolderAsync(((App)Application.Current).MainWindow);
            if (folder != null)
                ViewModel.ChosenParentFolder = folder;
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Failed to pick a folder for the AI text recognition");
        }
    }

    private void ButtonDefaultLocation_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UseDefaultLocation();
    }
}
