namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// Exception thrown when an IPC operation with the background agent fails.
/// </summary>
public class AgentException : Exception
{
    public string ErrorCode { get; }

    public AgentException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
