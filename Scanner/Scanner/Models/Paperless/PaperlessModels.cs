using System;
using System.Collections.Generic;

namespace Scanner.Models.Paperless;

/// <summary>
/// A named object on the Paperless-ngx server, e.g. a tag, correspondent, document type or storage path.
/// </summary>
public record PaperlessEntity(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The content of a saved project file, read into memory so it can be uploaded without holding project locks.
/// </summary>
public record PaperlessUploadFile(string FileName, byte[] Content, string ContentType);

/// <summary>
/// Optional metadata sent along with an upload. <see langword="null"/> values let Paperless-ngx decide on its own.
/// </summary>
public record PaperlessUploadMetadata(
    string? Title,
    DateTimeOffset? Created,
    int? CorrespondentId,
    int? DocumentTypeId,
    int? StoragePathId,
    IReadOnlyList<int> TagIds,
    int? ArchiveSerialNumber);

public enum PaperlessTaskState
{
    Pending,
    Success,
    Failure,
}

/// <summary>
/// The state of a consumption task on the Paperless-ngx server.
/// </summary>
public record PaperlessTaskResult(PaperlessTaskState State, int? DocumentId, string? Message);

public enum PaperlessConnectionError
{
    None,
    MissingInput,
    InvalidUrl,
    Unreachable,
    Timeout,
    Unauthorized,
    NotPaperless,
    Unknown,
}

/// <summary>
/// Result of a connection test against a Paperless-ngx server.
/// </summary>
public record PaperlessConnectionInfo(PaperlessConnectionError Error, string? ServerVersion)
{
    public bool IsSuccess => Error == PaperlessConnectionError.None;
}

/// <summary>
/// Thrown when a request to Paperless-ngx fails. <see cref="Error"/> categorizes the failure for the UI,
/// <see cref="Exception.Message"/> may contain the server's response for diagnostics.
/// </summary>
public class PaperlessException : Exception
{
    public PaperlessConnectionError Error { get; }

    public PaperlessException(PaperlessConnectionError error, string? message = null, Exception? innerException = null)
        : base(message ?? error.ToString(), innerException)
    {
        Error = error;
    }
}
