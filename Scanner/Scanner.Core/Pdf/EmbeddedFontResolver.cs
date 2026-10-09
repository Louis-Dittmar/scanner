using PdfSharp.Fonts;
using System;
using System.IO;

namespace Scanner.Core.Pdf;

/// <summary>
/// Serves Liberation Sans (metric-compatible with Arial) from the assembly's resources, so PDFs are created the same
/// way on Windows, in CI and in tests. Every family name resolves to it; italics are simulated.
/// </summary>
internal sealed class EmbeddedFontResolver : IFontResolver
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public const string FamilyName = "Scanner Sans";
    private const string regularFace = "LiberationSans-Regular";
    private const string boldFace = "LiberationSans-Bold";

    private static readonly object installLock = new();
    private static bool isInstalled;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Registers the resolver with PDFsharp once. PDFsharp only allows a single global resolver; nothing else in
    /// the app draws text into PDFs.
    /// </summary>
    public static void EnsureInstalled()
    {
        lock (installLock)
        {
            if (isInstalled)
                return;

            if (GlobalFontSettings.FontResolver is not EmbeddedFontResolver)
                GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
            isInstalled = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        return new FontResolverInfo(bold ? boldFace : regularFace, false, italic);
    }

    public byte[]? GetFont(string faceName)
    {
        string resource = $"Scanner.Core.Fonts.{faceName}.ttf";
        using Stream? stream = typeof(EmbeddedFontResolver).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Font resource {resource} is missing");
        using MemoryStream memory = new();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
