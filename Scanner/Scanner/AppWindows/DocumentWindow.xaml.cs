using CommunityToolkit.Mvvm.DependencyInjection;
using Scanner.Services.Interfaces;

namespace Scanner.AppWindows;

/// <summary>
/// Shows how the current document goes through white correction, AI text recognition and PDF creation.
/// </summary>
public sealed partial class DocumentWindow : WindowBase
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public DocumentWindow()
    {
        this.InitializeComponent();
        Ioc.Default.GetService<ILogService>()?.Log.Information("Window loaded");
        PersistenceId = "DocumentWindow";
        Title = Resources.Strings.Resources.DocumentWindowTitle;
    }
}
