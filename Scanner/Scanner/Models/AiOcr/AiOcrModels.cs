using System;
using System.Collections.Generic;

namespace Scanner.Models.AiOcr;

public enum AiOcrState
{
    /// <summary>Not available on this device (e.g. not x64).</summary>
    Unsupported,
    NotInstalled,
    Installing,
    Stopped,
    Starting,
    DownloadingModel,
    LoadingModel,
    Ready,
    Error,
}

/// <summary>
/// A recognized area of a page. Coordinates are normalized to 0..1000 relative to the page image.
/// </summary>
public record AiOcrRegion(string Label, int X1, int Y1, int X2, int Y2, string Text);

/// <summary>
/// The result of analyzing one page image.
/// </summary>
public record AiOcrPageResult(int Width, int Height, string Markdown, IReadOnlyList<AiOcrRegion> Regions);

public class AiOcrException : Exception
{
    public AiOcrException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
