using Microsoft.UI.Dispatching;

namespace Scanner.Services.Interfaces;

/// <summary>
/// Creates the additional AI outputs (Markdown and/or a searchable PDF) next to a project's saved file(s).
/// It watches the current project and runs a short while after it has been saved, so the original save flow
/// stays untouched. Page results are cached by image content, so auto-saves only analyze new or changed pages.
/// </summary>
public interface IAiOcrExportService
{
    /// <summary>
    /// Starts watching the current project. Called once on app launch.
    /// </summary>
    void Initialize(DispatcherQueue uiDispatcherQueue);
}
