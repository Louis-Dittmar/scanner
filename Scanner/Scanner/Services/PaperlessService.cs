using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Scanner.Models.Paperless;
using Scanner.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Storage;

namespace Scanner.Services;

internal partial class PaperlessService : ObservableObject, IPaperlessService
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Services
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    #region Constants
    private const string settingsContainerName = "Paperless";
    private const string serverUrlKey = "ServerUrl";
    private const string vaultResource = "Scanner.Paperless";
    private const string vaultUserName = "ApiToken";
    private const int listPageSize = 500;
    private static readonly TimeSpan connectionTestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan taskPollInterval = TimeSpan.FromSeconds(1.5);
    #endregion

    private readonly HttpClient httpClient = new()
    {
        // uploads of big PDFs over slow connections may take a while, individual calls use their own timeouts on top
        Timeout = TimeSpan.FromMinutes(10),
    };

    private string? serverUrl;
    public string? ServerUrl
    {
        get => serverUrl;
        private set
        {
            if (SetProperty(ref serverUrl, value))
                OnPropertyChanged(nameof(IsConfigured));
        }
    }

    private bool hasToken;
    public bool HasToken
    {
        get => hasToken;
        private set
        {
            if (SetProperty(ref hasToken, value))
                OnPropertyChanged(nameof(IsConfigured));
        }
    }

    public bool IsConfigured => ServerUrl != null && HasToken;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public PaperlessService()
    {
        // must never throw, the service may be resolved early
        try
        {
            if (GetSettingsContainer().Values[serverUrlKey] is string storedUrl && !string.IsNullOrWhiteSpace(storedUrl))
                serverUrl = storedUrl;

            hasToken = TryGetStoredToken() != null;
        }
        catch (Exception exc)
        {
            LogService?.Log.Warning(exc, "Failed to load Paperless configuration");
        }
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Configuration
    public void SaveConfiguration(string serverUrl, string? token)
    {
        string normalizedUrl = NormalizeServerUrl(serverUrl)
            ?? throw new PaperlessException(PaperlessConnectionError.InvalidUrl);

        GetSettingsContainer().Values[serverUrlKey] = normalizedUrl;
        ServerUrl = normalizedUrl;

        if (!string.IsNullOrWhiteSpace(token))
        {
            RemoveStoredToken();
            new PasswordVault().Add(new PasswordCredential(vaultResource, vaultUserName, token.Trim()));
            HasToken = true;
        }

        LogService?.Log.Information("Paperless connection saved");
    }

    public void RemoveConfiguration()
    {
        GetSettingsContainer().Values.Remove(serverUrlKey);
        ServerUrl = null;

        RemoveStoredToken();
        HasToken = false;

        LogService?.Log.Information("Paperless connection removed");
    }

    private static ApplicationDataContainer GetSettingsContainer()
    {
        return ApplicationData.Current.LocalSettings.CreateContainer(settingsContainerName, ApplicationDataCreateDisposition.Always);
    }

    private static string? TryGetStoredToken()
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(vaultResource, vaultUserName);
            credential.RetrievePassword();
            return string.IsNullOrEmpty(credential.Password) ? null : credential.Password;
        }
        catch (Exception)
        {
            // PasswordVault throws if no matching credential exists
            return null;
        }
    }

    private static void RemoveStoredToken()
    {
        try
        {
            PasswordVault vault = new();
            foreach (PasswordCredential credential in vault.FindAllByResource(vaultResource))
            {
                vault.Remove(credential);
            }
        }
        catch (Exception)
        {
            // PasswordVault throws if no matching credential exists
        }
    }

    /// <summary>
    /// Turns user input like <c>192.168.1.10:8000/</c> or <c>https://paperless.example.com/api</c> into a base address
    /// without trailing slash. Returns <see langword="null"/> if the input can't be used.
    /// </summary>
    internal static string? NormalizeServerUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string url = input.Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        return uri.GetLeftPart(UriPartial.Authority) + path;
    }
    #endregion

    #region Connection test
    public async Task<PaperlessConnectionInfo> TestConnectionAsync(string serverUrl, string? token, CancellationToken cancellationToken = default)
    {
        string? baseUrl = NormalizeServerUrl(serverUrl);
        string? effectiveToken = string.IsNullOrWhiteSpace(token) ? TryGetStoredToken() : token.Trim();

        if (string.IsNullOrWhiteSpace(serverUrl) || effectiveToken == null)
            return new(PaperlessConnectionError.MissingInput, null);
        if (baseUrl == null)
            return new(PaperlessConnectionError.InvalidUrl, null);

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(connectionTestTimeout);

        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, baseUrl, "/api/documents/?page_size=1", effectiveToken);
            using HttpResponseMessage response = await httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(PaperlessConnectionError.Unauthorized, null);

            if (!response.IsSuccessStatusCode)
                return new(PaperlessConnectionError.NotPaperless, null);

            // Paperless-ngx answers with a paginated JSON object and announces its version in a header
            string body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            if (!LooksLikePaginatedList(body))
                return new(PaperlessConnectionError.NotPaperless, null);

            string? version = response.Headers.TryGetValues("X-Version", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

            LogService?.Log.Information("Paperless connection test succeeded");
            return new(PaperlessConnectionError.None, version);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(PaperlessConnectionError.Timeout, null);
        }
        catch (HttpRequestException exc)
        {
            LogService?.Log.Warning(exc, "Paperless connection test failed");
            return new(PaperlessConnectionError.Unreachable, null);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogService?.Log.Warning(exc, "Paperless connection test failed");
            return new(PaperlessConnectionError.Unknown, null);
        }
    }

    private static bool LooksLikePaginatedList(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("results", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
    #endregion

    #region Lists
    public Task<IReadOnlyList<PaperlessEntity>> GetTagsAsync(CancellationToken cancellationToken = default)
        => GetEntitiesAsync("/api/tags/", cancellationToken);

    public Task<IReadOnlyList<PaperlessEntity>> GetCorrespondentsAsync(CancellationToken cancellationToken = default)
        => GetEntitiesAsync("/api/correspondents/", cancellationToken);

    public Task<IReadOnlyList<PaperlessEntity>> GetDocumentTypesAsync(CancellationToken cancellationToken = default)
        => GetEntitiesAsync("/api/document_types/", cancellationToken);

    public Task<IReadOnlyList<PaperlessEntity>> GetStoragePathsAsync(CancellationToken cancellationToken = default)
        => GetEntitiesAsync("/api/storage_paths/", cancellationToken);

    private async Task<IReadOnlyList<PaperlessEntity>> GetEntitiesAsync(string endpoint, CancellationToken cancellationToken)
    {
        (string baseUrl, string token) = GetConnectionOrThrow();
        List<PaperlessEntity> result = [];

        // page numbers instead of the server's "next" links, those may point to a different host behind a proxy
        for (int page = 1; ; page++)
        {
            string query = $"{endpoint}?page={page}&page_size={listPageSize}&ordering=name";
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, baseUrl, query, token);
            using HttpResponseMessage response = await SendOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
                throw new PaperlessException(PaperlessConnectionError.NotPaperless);

            foreach (JsonElement item in results.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int idValue)
                    && item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                {
                    result.Add(new PaperlessEntity(idValue, name.GetString() ?? ""));
                }
            }

            bool hasNext = document.RootElement.TryGetProperty("next", out JsonElement next) && next.ValueKind == JsonValueKind.String;
            if (!hasNext || results.GetArrayLength() == 0)
                break;
        }

        return result;
    }
    #endregion

    #region Upload
    public async Task<string> UploadDocumentAsync(PaperlessUploadFile file, PaperlessUploadMetadata metadata, CancellationToken cancellationToken = default)
    {
        (string baseUrl, string token) = GetConnectionOrThrow();

        // byte array based parts let MultipartFormDataContent compute the Content-Length Paperless-ngx requires
        using MultipartFormDataContent content = new();

        ByteArrayContent fileContent = new(file.Content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        content.Add(fileContent, "document", file.FileName);

        if (!string.IsNullOrWhiteSpace(metadata.Title))
            content.Add(new StringContent(metadata.Title.Trim()), "title");
        if (metadata.Created is DateTimeOffset created)
            content.Add(new StringContent(created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "created");
        if (metadata.CorrespondentId is int correspondentId)
            content.Add(new StringContent(correspondentId.ToString(CultureInfo.InvariantCulture)), "correspondent");
        if (metadata.DocumentTypeId is int documentTypeId)
            content.Add(new StringContent(documentTypeId.ToString(CultureInfo.InvariantCulture)), "document_type");
        if (metadata.StoragePathId is int storagePathId)
            content.Add(new StringContent(storagePathId.ToString(CultureInfo.InvariantCulture)), "storage_path");
        if (metadata.ArchiveSerialNumber is int asn)
            content.Add(new StringContent(asn.ToString(CultureInfo.InvariantCulture)), "archive_serial_number");
        foreach (int tagId in metadata.TagIds)
            content.Add(new StringContent(tagId.ToString(CultureInfo.InvariantCulture)), "tags");

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, baseUrl, "/api/documents/post_document/", token);
        request.Content = content;

        LogService?.Log.Information("Uploading document to Paperless ({Size} bytes, {TagCount} tags)", file.Content.Length, metadata.TagIds.Count);
        using HttpResponseMessage response = await SendOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        // the response body is the task UUID as a JSON string
        string body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        string? taskId = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.String)
                taskId = document.RootElement.GetString();
        }
        catch (JsonException)
        {
            taskId = body.Trim('"');
        }

        if (string.IsNullOrWhiteSpace(taskId))
            throw new PaperlessException(PaperlessConnectionError.NotPaperless, "Paperless didn't return a task ID");

        return taskId;
    }

    public async Task<PaperlessTaskResult> GetTaskResultAsync(string taskId, CancellationToken cancellationToken = default)
    {
        (string baseUrl, string token) = GetConnectionOrThrow();

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, baseUrl, $"/api/tasks/?task_id={Uri.EscapeDataString(taskId)}", token);
        using HttpResponseMessage response = await SendOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);

        // depending on the version, the endpoint returns a plain array or a paginated object
        JsonElement tasks = document.RootElement;
        if (tasks.ValueKind == JsonValueKind.Object && tasks.TryGetProperty("results", out JsonElement results))
            tasks = results;

        if (tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() == 0)
            return new(PaperlessTaskState.Pending, null, null);   // task not registered yet

        JsonElement task = tasks[0];
        string status = task.TryGetProperty("status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString()!.ToUpperInvariant()
            : "";
        string? message = task.TryGetProperty("result", out JsonElement resultElement) && resultElement.ValueKind == JsonValueKind.String
            ? resultElement.GetString()
            : null;

        int? documentId = null;
        if (task.TryGetProperty("related_document", out JsonElement related))
        {
            if (related.ValueKind == JsonValueKind.Number && related.TryGetInt32(out int number))
                documentId = number;
            else if (related.ValueKind == JsonValueKind.String && int.TryParse(related.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                documentId = parsed;
        }

        return status switch
        {
            "SUCCESS" => new(PaperlessTaskState.Success, documentId, message),
            "FAILURE" or "REVOKED" => new(PaperlessTaskState.Failure, documentId, message),
            _ => new(PaperlessTaskState.Pending, documentId, message),
        };
    }

    public async Task<PaperlessTaskResult> WaitForTaskAsync(string taskId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        PaperlessTaskResult result = new(PaperlessTaskState.Pending, null, null);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(taskPollInterval, cancellationToken).ConfigureAwait(false);

            result = await GetTaskResultAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (result.State != PaperlessTaskState.Pending)
            {
                LogService?.Log.Information("Paperless task finished with {State}", result.State);
                return result;
            }
        }

        LogService?.Log.Information("Paperless task still pending after timeout");
        return result;
    }

    public Uri? GetDocumentWebUri(int documentId)
    {
        if (ServerUrl == null)
            return null;

        return Uri.TryCreate($"{ServerUrl}/documents/{documentId}/details", UriKind.Absolute, out Uri? uri) ? uri : null;
    }
    #endregion

    #region HTTP helpers
    private (string BaseUrl, string Token) GetConnectionOrThrow()
    {
        string? token = TryGetStoredToken();
        if (ServerUrl == null || token == null)
            throw new PaperlessException(PaperlessConnectionError.MissingInput);

        return (ServerUrl, token);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string baseUrl, string pathAndQuery, string token)
    {
        HttpRequestMessage request = new(method, baseUrl + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendOrThrowAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exc) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PaperlessException(PaperlessConnectionError.Timeout, innerException: exc);
        }
        catch (HttpRequestException exc)
        {
            throw new PaperlessException(PaperlessConnectionError.Unreachable, innerException: exc);
        }

        if (response.IsSuccessStatusCode)
            return response;

        // keep the server's explanation (e.g. validation errors), but never the request itself
        string body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) { }

        HttpStatusCode statusCode = response.StatusCode;
        response.Dispose();

        PaperlessConnectionError error = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => PaperlessConnectionError.Unauthorized,
            HttpStatusCode.NotFound => PaperlessConnectionError.NotPaperless,
            _ => PaperlessConnectionError.Unknown,
        };

        string details = body.Length > 300 ? body[..300] + "…" : body;
        LogService?.Log.Warning("Paperless request failed with status {StatusCode}", (int)statusCode);
        throw new PaperlessException(error, $"HTTP {(int)statusCode}: {details}");
    }
    #endregion
}
