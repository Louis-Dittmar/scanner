using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Scanner.Services.Interfaces;
using Scanner.Services.Pipeline;
using System.IO;

namespace Scanner.ViewModels;

/// <summary>
/// Asks whether and where to set up the local AI text recognition.
/// </summary>
public partial class AiSetupDialogViewModel : ObservableObject
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private readonly IAiOcrService AiOcrService = Ioc.Default.GetRequiredService<IAiOcrService>();
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();

    /// <summary>
    /// The folder the user picked, <see langword="null"/> for the default location.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefaultLocation), nameof(IsCustomLocation), nameof(TargetFolderPath), nameof(SpaceText), nameof(HasEnoughSpace), nameof(HasNotEnoughSpace), nameof(RemovalText))]
    private string? chosenParentFolder;

    [ObservableProperty]
    private bool dontAskAgain;

    public bool IsDefaultLocation => ChosenParentFolder == null;
    public bool IsCustomLocation => ChosenParentFolder != null;
    public bool IsInstalled => AiOcrService.IsInstalled;
    public bool IsSupported => AiOcrService.IsSupported;
    public bool IsUnsupported => !AiOcrService.IsSupported;

    public string TargetFolderPath => ChosenParentFolder == null
        ? (AiOcrService.IsDefaultInstallLocation ? AiOcrService.InstallFolderPath : DefaultFolderPath)
        : Path.Combine(ChosenParentFolder, "Scanner Paperless KI");

    private string DefaultFolderPath => Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "AiOcr");

    public string SpaceText
    {
        get
        {
            string required = AiSetup.FormatGigabytes(AiSetup.RequiredBytes);
            return AiSetup.GetFreeSpace(TargetFolderPath) is long free
                ? string.Format(Resources.Strings.Resources.AiSetupSpace, required, AiSetup.FormatGigabytes(free))
                : string.Format(Resources.Strings.Resources.AiSetupSpaceUnknown, required);
        }
    }

    public bool HasEnoughSpace => AiSetup.GetFreeSpace(TargetFolderPath) is not long free || free >= AiSetup.RequiredBytes;
    public bool HasNotEnoughSpace => !HasEnoughSpace;

    public string RemovalText => IsDefaultLocation
        ? Resources.Strings.Resources.AiSetupRemovalDefault
        : Resources.Strings.Resources.AiSetupRemovalCustom;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public AiSetupDialogViewModel()
    {
        if (!AiOcrService.IsDefaultInstallLocation)
            ChosenParentFolder = Path.GetDirectoryName(AiOcrService.InstallFolderPath);
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public void UseDefaultLocation() => ChosenParentFolder = null;

    /// <summary>
    /// Stores the location and starts the installation in the background.
    /// </summary>
    public void Install(DispatcherQueue uiDispatcherQueue)
    {
        LogService?.Log.Information("AI text recognition setup accepted, default location: {IsDefault}", IsDefaultLocation);
        if (!AiOcrService.IsInstalled)
            AiOcrService.SetInstallLocation(ChosenParentFolder);
        AiSetup.StartInstallInBackground(uiDispatcherQueue);
    }

    /// <summary>
    /// Called when the dialog is closed without installing.
    /// </summary>
    public void Postpone()
    {
        LogService?.Log.Information("AI text recognition setup postponed, ask again: {AskAgain}", !DontAskAgain);
        if (DontAskAgain)
            AiOcrService.SetupPromptDismissed = true;
    }
}
