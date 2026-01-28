namespace GhcpLogsAnalyzer;

/// <summary>
/// Represents a log entry with its content.
/// </summary>
public record LogEntry
{
    /// <summary>
    /// Unique identifier or path for the log source.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Display name for the log entry.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The log content.
    /// </summary>
    public required string Content { get; init; }
}

/// <summary>
/// Interface for log sources that can provide log entries for analysis.
/// </summary>
public interface ILogSource
{
    /// <summary>
    /// Gets the name of this log source.
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Retrieves log entries from the source.
    /// </summary>
    /// <param name="location">Source-specific location identifier (e.g., folder path, connection string, URL).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of log entries.</returns>
    IAsyncEnumerable<LogEntry> GetLogsAsync(string location, CancellationToken cancellationToken = default);
}
