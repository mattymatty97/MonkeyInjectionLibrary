namespace InjectionLibrary;

/// <summary>
/// Defines strategies for handling errors during injection operations.
/// </summary>
public enum ErrorHandlingStrategy
{
    /// <summary>
    /// Immediately terminates the injection process when an error occurs.
    /// </summary>
    Terminate,

    /// <summary>
    /// Silently ignores errors and continues execution.
    /// </summary>
    Ignore,

    /// <summary>
    /// Logs the error as a warning and continues execution.
    /// </summary>
    LogWarning,

    /// <summary>
    /// Logs the error and continues execution.
    /// </summary>
    LogError,
}