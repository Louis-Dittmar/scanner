using System;

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

public class AiOcrException : Exception
{
    public AiOcrException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
