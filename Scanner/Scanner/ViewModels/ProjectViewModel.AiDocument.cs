using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Scanner.Services.Interfaces;

namespace Scanner.ViewModels;

partial class ProjectViewModel
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public readonly IDocumentPipelineService DocumentPipelineService = Ioc.Default.GetRequiredService<IDocumentPipelineService>();

    /// <summary>
    /// White correction, AI text recognition and a selectable PDF for the saved project (fork addition).
    /// </summary>
    public AsyncRelayCommand CreateAiDocumentAsyncCommand => new AsyncRelayCommand(DocumentPipelineService.ProcessCurrentProjectAsync);
}
