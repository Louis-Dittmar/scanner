using Scanner.Models.Paperless;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Scanner.Services.Interfaces;

/// <summary>
/// Talks to a Paperless-ngx server via its REST API. The server address is kept in the app's local settings,
/// the API token in the Windows Credential Locker (<see cref="Windows.Security.Credentials.PasswordVault"/>).
/// </summary>
public interface IPaperlessService : INotifyPropertyChanged
{
    /// <summary>
    /// The normalized server address (e.g. <c>http://192.168.1.10:8000</c>), or <see langword="null"/> if not set.
    /// </summary>
    string? ServerUrl { get; }

    bool HasToken { get; }

    /// <summary>
    /// <see langword="true"/> if both a server address and an API token are stored.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Stores the connection. Pass <see langword="null"/> or an empty <paramref name="token"/> to keep the stored token.
    /// </summary>
    void SaveConfiguration(string serverUrl, string? token);

    void RemoveConfiguration();

    /// <summary>
    /// Tests a connection. Pass <see langword="null"/> or an empty <paramref name="token"/> to use the stored token.
    /// </summary>
    Task<PaperlessConnectionInfo> TestConnectionAsync(string serverUrl, string? token, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaperlessEntity>> GetTagsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PaperlessEntity>> GetCorrespondentsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PaperlessEntity>> GetDocumentTypesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PaperlessEntity>> GetStoragePathsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a document and returns the ID of the consumption task Paperless-ngx started for it.
    /// </summary>
    Task<string> UploadDocumentAsync(PaperlessUploadFile file, PaperlessUploadMetadata metadata, CancellationToken cancellationToken = default);

    Task<PaperlessTaskResult> GetTaskResultAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls the task until it succeeds, fails or <paramref name="timeout"/> elapses (then returns a pending result).
    /// </summary>
    Task<PaperlessTaskResult> WaitForTaskAsync(string taskId, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// The address of a document in the Paperless-ngx web interface.
    /// </summary>
    Uri? GetDocumentWebUri(int documentId);
}
