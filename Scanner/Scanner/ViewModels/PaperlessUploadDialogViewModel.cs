using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Scanner.Models.Paperless;
using Scanner.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Scanner.ViewModels;

public enum PaperlessUploadState
{
    Loading,
    Ready,
    Uploading,
    Done,
    Failed,
}

public partial class PaperlessTagItem : ObservableObject
{
    public PaperlessEntity Tag { get; }

    public string Name => Tag.Name;

    [ObservableProperty]
    private bool isSelected;

    public PaperlessTagItem(PaperlessEntity tag)
    {
        Tag = tag;
    }
}

public partial class PaperlessUploadDialogViewModel : ObservableObject, IDisposable
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Services
    private readonly IPaperlessService PaperlessService = Ioc.Default.GetRequiredService<IPaperlessService>();
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    #region Constants
    /// <summary>
    /// Placeholder entry for "let Paperless-ngx decide" in the selection lists.
    /// </summary>
    private static readonly PaperlessEntity automaticEntity = new(-1, Resources.Strings.Resources.PaperlessAutomatic);
    private static readonly TimeSpan taskTimeout = TimeSpan.FromSeconds(90);
    #endregion

    public readonly IReadOnlyList<PaperlessUploadFile> Files;
    public readonly string SuggestedTitle;

    public bool IsHandlingMultipleFiles => Files.Count > 1;

    [ObservableProperty]
    private string title = "";

    [ObservableProperty]
    private DateTimeOffset? created;

    /// <summary>
    /// <see cref="double.NaN"/> if empty, as used by NumberBox.
    /// </summary>
    [ObservableProperty]
    private double archiveSerialNumber = double.NaN;

    [ObservableProperty]
    private List<PaperlessEntity> correspondents = [automaticEntity];

    [ObservableProperty]
    private PaperlessEntity selectedCorrespondent = automaticEntity;

    [ObservableProperty]
    private List<PaperlessEntity> documentTypes = [automaticEntity];

    [ObservableProperty]
    private PaperlessEntity selectedDocumentType = automaticEntity;

    [ObservableProperty]
    private List<PaperlessEntity> storagePaths = [automaticEntity];

    [ObservableProperty]
    private PaperlessEntity selectedStoragePath = automaticEntity;

    private List<PaperlessTagItem> allTags = [];

    [ObservableProperty]
    private List<PaperlessTagItem> filteredTags = [];

    [ObservableProperty]
    private string tagFilter = "";
    partial void OnTagFilterChanged(string value) => ApplyTagFilter();

    public int SelectedTagCount => allTags.Count(x => x.IsSelected);

    public string TagsHeaderText => SelectedTagCount > 0
        ? $"{Resources.Strings.Resources.PaperlessTags} ({SelectedTagCount})"
        : Resources.Strings.Resources.PaperlessTags;

    public bool HasNoTags => State != PaperlessUploadState.Loading && allTags.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsFormEnabled))]
    [NotifyPropertyChangedFor(nameof(IsUploading))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(CanUpload))]
    [NotifyPropertyChangedFor(nameof(HasNoTags))]
    [NotifyPropertyChangedFor(nameof(IsFormVisible))]
    [NotifyPropertyChangedFor(nameof(CloseButtonText))]
    private PaperlessUploadState state = PaperlessUploadState.Loading;

    public bool IsFormVisible => State is PaperlessUploadState.Ready or PaperlessUploadState.Uploading;
    public string CloseButtonText => State is PaperlessUploadState.Done or PaperlessUploadState.Failed
        ? Resources.Strings.Resources.Close
        : Resources.Strings.Resources.Cancel;

    public bool IsLoading => State == PaperlessUploadState.Loading;
    public bool IsFormEnabled => State == PaperlessUploadState.Ready;
    public bool IsUploading => State == PaperlessUploadState.Uploading;
    public bool IsDone => State == PaperlessUploadState.Done;
    public bool CanUpload => State == PaperlessUploadState.Ready;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorText;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocumentUri))]
    private Uri? documentUri;

    public bool HasDocumentUri => DocumentUri != null;

    [ObservableProperty]
    private string resultText = "";

    private readonly CancellationTokenSource lifetimeCancellation = new();


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public PaperlessUploadDialogViewModel(IReadOnlyList<PaperlessUploadFile> files, string suggestedTitle)
    {
        Files = files;
        SuggestedTitle = suggestedTitle;
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public void Dispose()
    {
        // don't cancel a running upload, closing the dialog must not leave a half-sent document behind
        foreach (PaperlessTagItem item in allTags)
            item.PropertyChanged -= TagItem_PropertyChanged;
    }

    /// <summary>
    /// Loads tags, correspondents, document types and storage paths. A list that fails to load (e.g. due to missing
    /// permissions) simply stays empty; only if everything fails, the dialog shows an error.
    /// </summary>
    public async Task LoadAsync()
    {
        State = PaperlessUploadState.Loading;
        StatusText = Resources.Strings.Resources.PaperlessLoadingMetadata;
        ErrorText = null;

        CancellationToken token = lifetimeCancellation.Token;
        Task<IReadOnlyList<PaperlessEntity>> tagsTask = PaperlessService.GetTagsAsync(token);
        Task<IReadOnlyList<PaperlessEntity>> correspondentsTask = PaperlessService.GetCorrespondentsAsync(token);
        Task<IReadOnlyList<PaperlessEntity>> documentTypesTask = PaperlessService.GetDocumentTypesAsync(token);
        Task<IReadOnlyList<PaperlessEntity>> storagePathsTask = PaperlessService.GetStoragePathsAsync(token);

        try
        {
            await Task.WhenAll(tagsTask, correspondentsTask, documentTypesTask, storagePathsTask);
        }
        catch (Exception)
        {
            // handled per task below
        }

        PaperlessException? firstError = null;
        IReadOnlyList<PaperlessEntity> GetResultOrEmpty(Task<IReadOnlyList<PaperlessEntity>> task)
        {
            if (task.IsCompletedSuccessfully)
                return task.Result;

            if (task.Exception?.GetBaseException() is Exception exc)
            {
                LogService?.Log.Warning(exc, "Failed to load a Paperless list");
                firstError ??= exc as PaperlessException ?? new PaperlessException(PaperlessConnectionError.Unknown, innerException: exc);
            }
            return [];
        }

        IReadOnlyList<PaperlessEntity> tags = GetResultOrEmpty(tagsTask);
        Correspondents = [automaticEntity, .. GetResultOrEmpty(correspondentsTask)];
        DocumentTypes = [automaticEntity, .. GetResultOrEmpty(documentTypesTask)];
        StoragePaths = [automaticEntity, .. GetResultOrEmpty(storagePathsTask)];
        SelectedCorrespondent = automaticEntity;
        SelectedDocumentType = automaticEntity;
        SelectedStoragePath = automaticEntity;

        allTags = tags.Select(x => new PaperlessTagItem(x)).ToList();
        foreach (PaperlessTagItem item in allTags)
            item.PropertyChanged += TagItem_PropertyChanged;
        ApplyTagFilter();

        bool everythingFailed = new[] { tagsTask, correspondentsTask, documentTypesTask, storagePathsTask }.All(x => !x.IsCompletedSuccessfully);
        if (everythingFailed && firstError != null)
        {
            ErrorText = PaperlessTexts.GetErrorText(firstError.Error);
            State = PaperlessUploadState.Failed;
            return;
        }

        StatusText = "";
        State = PaperlessUploadState.Ready;
    }

    /// <summary>
    /// Uploads all files and waits for Paperless-ngx to process them.
    /// </summary>
    public async Task UploadAsync()
    {
        if (State != PaperlessUploadState.Ready)
            return;

        State = PaperlessUploadState.Uploading;
        ErrorText = null;

        PaperlessUploadMetadata metadata = CreateMetadata();
        CancellationToken token = lifetimeCancellation.Token;

        try
        {
            // upload everything first, then wait, so Paperless can work on all documents in parallel
            List<string> taskIds = [];
            for (int i = 0; i < Files.Count; i++)
            {
                StatusText = IsHandlingMultipleFiles
                    ? $"{Resources.Strings.Resources.PaperlessUploading} ({i + 1}/{Files.Count})"
                    : Resources.Strings.Resources.PaperlessUploading;

                PaperlessUploadMetadata fileMetadata = metadata;
                if (IsHandlingMultipleFiles && metadata.Title != null)
                    fileMetadata = metadata with { Title = $"{metadata.Title} ({i + 1})" };

                taskIds.Add(await PaperlessService.UploadDocumentAsync(Files[i], fileMetadata, token));
            }

            StatusText = Resources.Strings.Resources.PaperlessProcessing;
            List<PaperlessTaskResult> results = [];
            foreach (string taskId in taskIds)
            {
                results.Add(await PaperlessService.WaitForTaskAsync(taskId, taskTimeout, token));
            }

            ShowResults(results);
        }
        catch (PaperlessException exc)
        {
            LogService?.Log.Warning(exc, "Paperless upload failed");
            ErrorText = PaperlessTexts.GetErrorText(exc.Error) + Environment.NewLine + exc.Message;
            State = PaperlessUploadState.Ready;     // allow retrying
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogService?.Log.Error(exc, "Paperless upload failed");
            ErrorText = Resources.Strings.Resources.PaperlessErrorUnknown;
            State = PaperlessUploadState.Ready;
        }
        finally
        {
            if (State != PaperlessUploadState.Uploading)
                StatusText = "";
        }
    }

    private void ShowResults(List<PaperlessTaskResult> results)
    {
        List<PaperlessTaskResult> failures = results.Where(x => x.State == PaperlessTaskState.Failure).ToList();
        if (failures.Count > 0)
        {
            // e.g. duplicates are rejected by Paperless with an explanation
            ErrorText = string.Join(Environment.NewLine, failures.Select(x => x.Message ?? Resources.Strings.Resources.PaperlessErrorUnknown));
        }

        bool anyPending = results.Any(x => x.State == PaperlessTaskState.Pending);
        int? firstDocumentId = results.FirstOrDefault(x => x.DocumentId != null)?.DocumentId;
        DocumentUri = firstDocumentId is int id ? PaperlessService.GetDocumentWebUri(id) : null;

        if (failures.Count == results.Count)
        {
            ResultText = "";
            State = PaperlessUploadState.Failed;
            return;
        }

        ResultText = anyPending
            ? Resources.Strings.Resources.PaperlessUploadPendingBody
            : Resources.Strings.Resources.PaperlessUploadSuccessBody;
        State = PaperlessUploadState.Done;
    }

    private PaperlessUploadMetadata CreateMetadata()
    {
        int? asn = double.IsNaN(ArchiveSerialNumber) || ArchiveSerialNumber < 0 ? null : (int)Math.Round(ArchiveSerialNumber);

        return new PaperlessUploadMetadata(
            Title: string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
            Created: Created,
            CorrespondentId: SelectedCorrespondent.Id >= 0 ? SelectedCorrespondent.Id : null,
            DocumentTypeId: SelectedDocumentType.Id >= 0 ? SelectedDocumentType.Id : null,
            StoragePathId: SelectedStoragePath.Id >= 0 ? SelectedStoragePath.Id : null,
            TagIds: allTags.Where(x => x.IsSelected).Select(x => x.Tag.Id).ToList(),
            ArchiveSerialNumber: asn);
    }

    private void ApplyTagFilter()
    {
        string filter = TagFilter.Trim();

        // keep selected tags visible so the user always sees what will be sent
        FilteredTags = string.IsNullOrEmpty(filter)
            ? allTags
            : allTags.Where(x => x.IsSelected || x.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

        OnPropertyChanged(nameof(HasNoTags));
    }

    private void TagItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaperlessTagItem.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedTagCount));
            OnPropertyChanged(nameof(TagsHeaderText));
        }
    }

    /// <summary>
    /// Selection lists may not be bound to <see langword="null"/>, so the views fall back to the automatic entry.
    /// </summary>
    partial void OnSelectedCorrespondentChanged(PaperlessEntity value)
    {
        if (value == null) SelectedCorrespondent = automaticEntity;
    }

    partial void OnSelectedDocumentTypeChanged(PaperlessEntity value)
    {
        if (value == null) SelectedDocumentType = automaticEntity;
    }

    partial void OnSelectedStoragePathChanged(PaperlessEntity value)
    {
        if (value == null) SelectedStoragePath = automaticEntity;
    }
}
