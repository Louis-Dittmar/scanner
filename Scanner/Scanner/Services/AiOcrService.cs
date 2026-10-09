using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Scanner.Models.AiOcr;
using Scanner.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Storage;

namespace Scanner.Services;

internal partial class AiOcrService : ObservableObject, IAiOcrService
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Services
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();
    #endregion

    #region Constants
    private const string uvVersion = "0.12.24";
    private const string uvDownloadUrl = $"https://github.com/astral-sh/uv/releases/download/{uvVersion}/uv-x86_64-pc-windows-msvc.zip";
    private const string pythonVersion = "3.12";
    private const string settingsContainerName = "AiOcr";
    private const string vaultResource = "Scanner.AiOcr";
    private const string vaultUserName = "ServiceToken";
    private const int idleTimeoutSeconds = 60 * 60;
    private static readonly TimeSpan serviceStartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan modelReadyTimeout = TimeSpan.FromMinutes(45);    // includes the 7 GB download
    private static readonly TimeSpan pageTimeout = TimeSpan.FromMinutes(10);
    #endregion

    private readonly HttpClient httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim lifecycleSemaphore = new(1, 1);
    private int? servicePort;
    private Process? serviceProcess;

    #region Paths
    public string InstallFolderPath { get; }
    private string UvExePath => Path.Combine(InstallFolderPath, "uv", "uv.exe");
    private string PythonFolderPath => Path.Combine(InstallFolderPath, "python");
    private string UvCacheFolderPath => Path.Combine(InstallFolderPath, "uv-cache");
    private string VenvFolderPath => Path.Combine(InstallFolderPath, "venv");
    private string VenvPythonPath => Path.Combine(VenvFolderPath, "Scripts", "python.exe");
    private string HuggingFaceFolderPath => Path.Combine(InstallFolderPath, "huggingface");
    private string StateFilePath => Path.Combine(InstallFolderPath, "service.json");
    private string LogFilePath => Path.Combine(InstallFolderPath, "service.log");
    private string InstallMarkerPath => Path.Combine(InstallFolderPath, "installed.json");
    private static string ServiceSourceFolderPath => Path.Combine(AppContext.BaseDirectory, "OcrService");
    #endregion

    public bool IsSupported { get; } = RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public bool IsInstalled => IsSupported && File.Exists(VenvPythonPath) && ReadInstallMarker() == GetRequirementsHash();

    [ObservableProperty]
    private AiOcrState state;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private double? progress;

    [ObservableProperty]
    private string? errorDetails;

    #region Settings
    public bool IsEnabled
    {
        get => GetSetting(nameof(IsEnabled), false);
        set
        {
            if (SetSetting(nameof(IsEnabled), value))
                LogService?.Log.Information("AI text recognition enabled: {Value}", value);
        }
    }

    public bool OutputMarkdown
    {
        get => GetSetting(nameof(OutputMarkdown), true);
        set => SetSetting(nameof(OutputMarkdown), value);
    }

    public bool OutputPdf
    {
        get => GetSetting(nameof(OutputPdf), true);
        set => SetSetting(nameof(OutputPdf), value);
    }

    public int LingerMinutes
    {
        get => GetSetting(nameof(LingerMinutes), 5);
        set => SetSetting(nameof(LingerMinutes), Math.Clamp(value, 0, 240));
    }
    #endregion


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public AiOcrService()
    {
        // must never throw
        InstallFolderPath = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "AiOcr");
        try
        {
            UpdateIdleState();
        }
        catch (Exception exc)
        {
            LogService?.Log.Warning(exc, "Failed to determine AI text recognition state");
            State = AiOcrState.Error;
        }
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Settings helpers
    private static ApplicationDataContainer SettingsContainer =>
        ApplicationData.Current.LocalSettings.CreateContainer(settingsContainerName, ApplicationDataCreateDisposition.Always);

    private static T GetSetting<T>(string name, T defaultValue)
    {
        return SettingsContainer.Values[name] is T value ? value : defaultValue;
    }

    private bool SetSetting<T>(string name, T value)
    {
        if (SettingsContainer.Values[name] is T current && EqualityComparer<T>.Default.Equals(current, value))
            return false;

        SettingsContainer.Values[name] = value;
        OnPropertyChanged(name);
        return true;
    }
    #endregion

    #region State helpers
    private void SetState(AiOcrState newState, string text, double? newProgress = null, string? details = null)
    {
        State = newState;
        StatusText = text;
        Progress = newProgress;
        ErrorDetails = details;
    }

    private void UpdateIdleState()
    {
        if (!IsSupported)
            SetState(AiOcrState.Unsupported, Resources.Strings.Resources.AiOcrStateUnsupported);
        else if (!IsInstalled)
            SetState(AiOcrState.NotInstalled, Resources.Strings.Resources.AiOcrStateNotInstalled);
        else
            SetState(AiOcrState.Stopped, Resources.Strings.Resources.AiOcrStateStopped);

        OnPropertyChanged(nameof(IsInstalled));
    }
    #endregion

    #region Installation
    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new AiOcrException("AI text recognition requires an x64 device");

        await lifecycleSemaphore.WaitAsync(cancellationToken);
        try
        {
            await StopServiceAsync();
            LogService?.Log.Information("Installing AI text recognition");
            Directory.CreateDirectory(InstallFolderPath);
            File.Delete(InstallMarkerPath);

            // 1. uv, a single executable that installs Python and packages
            SetState(AiOcrState.Installing, Resources.Strings.Resources.AiOcrInstallDownloadingTools, 0.02);
            await DownloadUvAsync(cancellationToken);

            // 2. a private Python, independent of anything installed on the system
            SetState(AiOcrState.Installing, Resources.Strings.Resources.AiOcrInstallPython, 0.08);
            await RunUvAsync(["python", "install", pythonVersion], cancellationToken);

            SetState(AiOcrState.Installing, Resources.Strings.Resources.AiOcrInstallPython, 0.12);
            await RunUvAsync(["venv", VenvFolderPath, "--python", pythonVersion, "--python-preference", "only-managed", "--clear"], cancellationToken);

            // 3. PyTorch with CUDA (about 3 GB), then the remaining packages
            SetState(AiOcrState.Installing, Resources.Strings.Resources.AiOcrInstallPyTorch, 0.15);
            await RunUvAsync(["pip", "install", "--python", VenvPythonPath, "-r", Path.Combine(ServiceSourceFolderPath, "requirements-torch.txt")], cancellationToken);

            SetState(AiOcrState.Installing, Resources.Strings.Resources.AiOcrInstallPackages, 0.85);
            await RunUvAsync(["pip", "install", "--python", VenvPythonPath, "-r", Path.Combine(ServiceSourceFolderPath, "requirements.txt")], cancellationToken);

            await File.WriteAllTextAsync(InstallMarkerPath, JsonSerializer.Serialize(new { requirements = GetRequirementsHash() }), cancellationToken);
            LogService?.Log.Information("AI text recognition installed");
            UpdateIdleState();
        }
        catch (OperationCanceledException)
        {
            UpdateIdleState();
            throw;
        }
        catch (Exception exc)
        {
            LogService?.Log.Error(exc, "Installing AI text recognition failed");
            SetState(AiOcrState.Error, Resources.Strings.Resources.AiOcrInstallFailed, details: exc.Message);
            OnPropertyChanged(nameof(IsInstalled));
            throw;
        }
        finally
        {
            lifecycleSemaphore.Release();
        }
    }

    public async Task UninstallAsync()
    {
        await lifecycleSemaphore.WaitAsync();
        try
        {
            await StopServiceAsync();
            if (Directory.Exists(InstallFolderPath))
            {
                // the service may need a moment to release its files
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        Directory.Delete(InstallFolderPath, recursive: true);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        await Task.Delay(1000);
                    }
                }
            }
            LogService?.Log.Information("AI text recognition uninstalled");
        }
        finally
        {
            UpdateIdleState();
            lifecycleSemaphore.Release();
        }
    }

    private async Task DownloadUvAsync(CancellationToken cancellationToken)
    {
        string zipPath = Path.Combine(InstallFolderPath, "uv.zip");
        using (HttpResponseMessage response = await httpClient.GetAsync(uvDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using FileStream file = File.Create(zipPath);
            await response.Content.CopyToAsync(file, cancellationToken);
        }

        string uvFolder = Path.GetDirectoryName(UvExePath)!;
        if (Directory.Exists(uvFolder))
            Directory.Delete(uvFolder, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, uvFolder, overwriteFiles: true);
        File.Delete(zipPath);

        if (!File.Exists(UvExePath))
            throw new AiOcrException("uv.exe not found in the downloaded archive");
    }

    private async Task RunUvAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(UvExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["UV_PYTHON_INSTALL_DIR"] = PythonFolderPath;
        startInfo.Environment["UV_CACHE_DIR"] = UvCacheFolderPath;
        startInfo.Environment["UV_NO_PROGRESS"] = "1";
        startInfo.Environment["UV_LINK_MODE"] = "copy";

        // keep the end of the output for error messages
        Queue<string> tail = new();
        void Collect(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 15)
                    tail.Dequeue();
            }
        }

        using Process process = new() { StartInfo = startInfo };
        process.OutputDataReceived += (s, e) => Collect(e.Data);
        process.ErrorDataReceived += (s, e) => Collect(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string output;
            lock (tail)
                output = string.Join(Environment.NewLine, tail);
            throw new AiOcrException($"uv {startInfo.ArgumentList.FirstOrDefault()} failed ({process.ExitCode}):{Environment.NewLine}{output}");
        }
    }

    private static string GetRequirementsHash()
    {
        StringBuilder content = new();
        foreach (string name in new[] { "requirements-torch.txt", "requirements.txt" })
        {
            string path = Path.Combine(ServiceSourceFolderPath, name);
            content.Append(File.Exists(path) ? File.ReadAllText(path) : name);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
    }

    private string? ReadInstallMarker()
    {
        try
        {
            if (!File.Exists(InstallMarkerPath))
                return null;
            return JsonNode.Parse(File.ReadAllText(InstallMarkerPath))?["requirements"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }
    #endregion

    #region Service lifecycle
    public async Task StartIfEnabledAsync()
    {
        if (!IsEnabled || !IsInstalled)
            return;

        try
        {
            await lifecycleSemaphore.WaitAsync();
            try
            {
                await ConnectOrStartAsync(CancellationToken.None);
            }
            finally
            {
                lifecycleSemaphore.Release();
            }

            // follow the model loading in the background, so the settings page shows the progress
            _ = WaitForModelAsync(CancellationToken.None);
        }
        catch (Exception exc)
        {
            LogService?.Log.Warning(exc, "Starting AI text recognition failed");
        }
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
            return false;

        await lifecycleSemaphore.WaitAsync(cancellationToken);
        try
        {
            await ConnectOrStartAsync(cancellationToken);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogService?.Log.Warning(exc, "Starting AI text recognition failed");
            SetState(AiOcrState.Error, Resources.Strings.Resources.AiOcrStartFailed, details: exc.Message);
            return false;
        }
        finally
        {
            lifecycleSemaphore.Release();
        }

        return await WaitForModelAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        await lifecycleSemaphore.WaitAsync();
        try
        {
            await StopServiceAsync();
        }
        finally
        {
            UpdateIdleState();
            lifecycleSemaphore.Release();
        }
    }

    /// <summary>
    /// Reuses a service that is still running (e.g. the app was reopened within the linger time) or starts a new one.
    /// Only called while holding <see cref="lifecycleSemaphore"/>.
    /// </summary>
    private async Task ConnectOrStartAsync(CancellationToken cancellationToken)
    {
        string token = GetOrCreateToken();

        // already connected?
        if (servicePort is int knownPort && await TryGetHealthAsync(knownPort, token, cancellationToken) != null)
            return;

        // service from a previous app session still running?
        if (TryReadStateFile() is { } existing && await TryGetHealthAsync(existing.Port, token, cancellationToken) != null)
        {
            servicePort = existing.Port;
            UpdateStateFileAppPid();
            LogService?.Log.Information("Reconnected to running AI text recognition service");
            return;
        }

        // start a new one
        SetState(AiOcrState.Starting, Resources.Strings.Resources.AiOcrStateStarting);
        servicePort = null;
        File.Delete(StateFilePath);

        ProcessStartInfo startInfo = new(VenvPythonPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = InstallFolderPath,
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(Path.Combine(ServiceSourceFolderPath, "server.py"));
        startInfo.ArgumentList.Add("--state-file");
        startInfo.ArgumentList.Add(StateFilePath);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("--linger");
        startInfo.ArgumentList.Add((LingerMinutes * 60).ToString());
        startInfo.ArgumentList.Add("--idle-timeout");
        startInfo.ArgumentList.Add(idleTimeoutSeconds.ToString());
        startInfo.ArgumentList.Add("--log-file");
        startInfo.ArgumentList.Add(LogFilePath);

        startInfo.Environment["SCANNER_OCR_TOKEN"] = token;
        startInfo.Environment["HF_HOME"] = HuggingFaceFolderPath;
        startInfo.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";

        serviceProcess = Process.Start(startInfo) ?? throw new AiOcrException("Python could not be started");
        LogService?.Log.Information("AI text recognition service started");

        // the service writes its port into the state file once it listens
        DateTime deadline = DateTime.UtcNow + serviceStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (serviceProcess.HasExited)
                throw new AiOcrException($"The service exited with code {serviceProcess.ExitCode}. {ReadLogTail()}");

            if (TryReadStateFile() is { } started && started.Pid == serviceProcess.Id)
            {
                servicePort = started.Port;
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new AiOcrException($"The service didn't start in time. {ReadLogTail()}");
    }

    /// <summary>
    /// Polls the service until the model is loaded, reflecting download and loading progress in <see cref="State"/>.
    /// </summary>
    private async Task<bool> WaitForModelAsync(CancellationToken cancellationToken)
    {
        string token = GetOrCreateToken();
        DateTime deadline = DateTime.UtcNow + modelReadyTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (servicePort is not int port)
                return false;

            JsonNode? health = await TryGetHealthAsync(port, token, cancellationToken);
            if (health == null)
            {
                SetState(AiOcrState.Error, Resources.Strings.Resources.AiOcrStartFailed, details: ReadLogTail());
                servicePort = null;
                return false;
            }

            string serviceState = health["state"]?.GetValue<string>() ?? "";
            string message = health["message"]?.GetValue<string>() ?? "";
            switch (serviceState)
            {
                case "ready":
                    SetState(AiOcrState.Ready, $"{Resources.Strings.Resources.AiOcrStateReady} ({message})");
                    return true;
                case "error":
                    SetState(AiOcrState.Error, Resources.Strings.Resources.AiOcrStartFailed, details: message);
                    return false;
                case "downloading":
                    SetState(AiOcrState.DownloadingModel, Resources.Strings.Resources.AiOcrStateDownloadingModel);
                    break;
                default:
                    SetState(AiOcrState.LoadingModel, Resources.Strings.Resources.AiOcrStateLoadingModel);
                    break;
            }

            await Task.Delay(2000, cancellationToken);
        }

        SetState(AiOcrState.Error, Resources.Strings.Resources.AiOcrStartFailed, details: "Timeout");
        return false;
    }

    private async Task StopServiceAsync()
    {
        string token = GetOrCreateToken();
        int? port = servicePort ?? TryReadStateFile()?.Port;
        if (port is int knownPort)
        {
            try
            {
                using HttpRequestMessage request = CreateRequest(HttpMethod.Post, knownPort, "/shutdown", token);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                using HttpResponseMessage response = await httpClient.SendAsync(request, timeout.Token);
            }
            catch (Exception) { }
        }

        if (serviceProcess is { HasExited: false })
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                await serviceProcess.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { serviceProcess.Kill(entireProcessTree: true); } catch (Exception) { }
            }
        }

        serviceProcess = null;
        servicePort = null;
        LogService?.Log.Information("AI text recognition service stopped");
    }
    #endregion

    #region Requests
    public async Task<AiOcrPageResult> AnalyzeAsync(byte[] image, CancellationToken cancellationToken = default)
    {
        if (servicePort is not int port)
            throw new AiOcrException("The service isn't running");

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, port, "/ocr", GetOrCreateToken());
        request.Content = new ByteArrayContent(image);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(pageTimeout);

        using HttpResponseMessage response = await httpClient.SendAsync(request, timeout.Token);
        string body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new AiOcrException($"HTTP {(int)response.StatusCode}: {body}");

        JsonNode root = JsonNode.Parse(body) ?? throw new AiOcrException("Empty response");
        List<AiOcrRegion> regions = [];
        foreach (JsonNode? region in root["regions"]?.AsArray() ?? new JsonArray())
        {
            JsonArray? box = region?["box"]?.AsArray();
            if (region == null || box == null || box.Count != 4)
                continue;

            regions.Add(new AiOcrRegion(
                region["label"]?.GetValue<string>() ?? "text",
                box[0]!.GetValue<int>(), box[1]!.GetValue<int>(), box[2]!.GetValue<int>(), box[3]!.GetValue<int>(),
                region["text"]?.GetValue<string>() ?? ""));
        }

        return new AiOcrPageResult(
            root["width"]?.GetValue<int>() ?? 0,
            root["height"]?.GetValue<int>() ?? 0,
            root["markdown"]?.GetValue<string>() ?? "",
            regions);
    }

    private async Task<JsonNode?> TryGetHealthAsync(int port, string token, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, port, "/health", token);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using HttpResponseMessage response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception exc) when (exc is HttpRequestException or JsonException
            || (exc is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, int port, string path, string token)
    {
        HttpRequestMessage request = new(method, $"http://127.0.0.1:{port}{path}");
        request.Headers.Add("X-Token", token);
        return request;
    }
    #endregion

    #region Files and secrets
    private (int Port, int Pid)? TryReadStateFile()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return null;
            JsonNode? node = JsonNode.Parse(File.ReadAllText(StateFilePath));
            int? port = node?["port"]?.GetValue<int>();
            int? pid = node?["pid"]?.GetValue<int>();
            return port is int p && pid is int i ? (p, i) : null;
        }
        catch (Exception)
        {
            return null;    // may be in the middle of being written
        }
    }

    /// <summary>
    /// Lets a service that was started by a previous app session follow this app instance.
    /// </summary>
    private void UpdateStateFileAppPid()
    {
        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(StateFilePath));
            if (node == null)
                return;
            node["appPid"] = Environment.ProcessId;
            File.WriteAllText(StateFilePath, node.ToJsonString());
        }
        catch (Exception exc)
        {
            LogService?.Log.Warning(exc, "Failed to update AI text recognition state file");
        }
    }

    private string ReadLogTail()
    {
        try
        {
            if (!File.Exists(LogFilePath))
                return "";
            using FileStream stream = new(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            string[] lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", lines.TakeLast(10)).Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// The token protecting the local service, kept in the Credential Locker so a reopened app can reconnect.
    /// </summary>
    private static string GetOrCreateToken()
    {
        PasswordVault vault = new();
        try
        {
            PasswordCredential credential = vault.Retrieve(vaultResource, vaultUserName);
            credential.RetrievePassword();
            if (!string.IsNullOrEmpty(credential.Password))
                return credential.Password;
        }
        catch (Exception)
        {
            // PasswordVault throws if no matching credential exists
        }

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        vault.Add(new PasswordCredential(vaultResource, vaultUserName, token));
        return token;
    }
    #endregion
}
