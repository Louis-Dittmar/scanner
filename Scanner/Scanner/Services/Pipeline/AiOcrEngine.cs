using CommunityToolkit.Mvvm.DependencyInjection;
using Scanner.Core.Ocr;
using Scanner.Core.Pipeline;
using Scanner.Services.Interfaces;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Scanner.Services.Pipeline;

/// <summary>
/// Runs the recognition through the local Unlimited-OCR service. Raw results are cached by image content, so
/// processing a document again (e.g. with another PDF mode or after an auto-save) doesn't analyze unchanged pages.
/// </summary>
internal sealed class AiOcrEngine : IOcrEngine
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private readonly IAiOcrService AiOcrService = Ioc.Default.GetRequiredService<IAiOcrService>();
    private readonly ILogService? LogService = Ioc.Default.GetService<ILogService>();

    private record CacheEntry(string Raw, int Width, int Height);

    private string CacheFolderPath => Path.Combine(AiOcrService.InstallFolderPath, "cache");


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public async Task<OcrPageResult> AnalyzeAsync(byte[] encodedImage, int width, int height, CancellationToken cancellationToken)
    {
        string hash = Convert.ToHexString(SHA256.HashData(encodedImage));
        if (await TryLoadAsync(hash) is { } cached)
            return UnlimitedOcrParser.Parse(cached.Raw, width, height);

        if (!await AiOcrService.EnsureReadyAsync(cancellationToken))
            throw new OcrException(AiOcrService.ErrorDetails ?? AiOcrService.StatusText);

        string raw = await AiOcrService.AnalyzeRawAsync(encodedImage, cancellationToken);
        await SaveAsync(hash, new CacheEntry(raw, width, height));
        return UnlimitedOcrParser.Parse(raw, width, height);
    }

    private async Task<CacheEntry?> TryLoadAsync(string hash)
    {
        string path = Path.Combine(CacheFolderPath, hash + ".json");
        if (!File.Exists(path))
            return null;

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CacheEntry>(stream);
        }
        catch (Exception exc)
        {
            LogService?.Log.Warning(exc, "Ignoring unreadable recognition cache entry");
            return null;
        }
    }

    private async Task SaveAsync(string hash, CacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(CacheFolderPath);
            await using FileStream stream = File.Create(Path.Combine(CacheFolderPath, hash + ".json"));
            await JsonSerializer.SerializeAsync(stream, entry);
        }
        catch (Exception exc)
        {
            LogService?.Log.Warning(exc, "Failed to cache recognition result");
        }
    }
}

/// <summary>
/// Stands in for the model during UI tests (app started with <c>--ui-test</c>), so the whole document pipeline can
/// be exercised on machines without a GPU. Reports a title, a paragraph and a picture.
/// </summary>
internal sealed class UiTestOcrEngine : IOcrEngine
{
    public async Task<OcrPageResult> AnalyzeAsync(byte[] encodedImage, int width, int height, CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);
        return UnlimitedOcrParser.Parse("""
            <|det|>title [80, 40, 920, 90]<|/det|># Testdokument
            <|det|>text [80, 120, 920, 300]<|/det|>Dieser Text stammt aus dem Testmodus der Texterkennung.
            <|det|>image [100, 350, 500, 600]<|/det|>
            """, width, height);
    }
}
