namespace GhcpLogsAnalyzer;

/// <summary>
/// Log source that scans a local file system folder for log files.
/// </summary>
public class FileSystemLogSource : ILogSource
{
    private static readonly string[] DefaultExtensions = [".log", ".txt"];

    private readonly string[] _supportedExtensions;

    public FileSystemLogSource(string[]? supportedExtensions = null)
    {
        _supportedExtensions = supportedExtensions ?? DefaultExtensions;
    }

    /// <inheritdoc />
    public string SourceName => "FileSystem";

    /// <inheritdoc />
    public async IAsyncEnumerable<LogEntry> GetLogsAsync(
        string location,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(location))
        {
            throw new DirectoryNotFoundException($"Log folder not found: {location}");
        }

        var logFiles = Directory.EnumerateFiles(location, "*.*", SearchOption.AllDirectories)
            .Where(f => _supportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        foreach (var filePath in logFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = await File.ReadAllTextAsync(filePath, cancellationToken);

            yield return new LogEntry
            {
                SourceId = filePath,
                Name = Path.GetFileName(filePath),
                Content = content
            };
        }
    }
}

/// <summary>
/// Legacy alias for FileSystemLogSource. Use <see cref="FileSystemLogSource"/> instead.
/// </summary>
[Obsolete("Use FileSystemLogSource instead")]
public class LogScanner : FileSystemLogSource
{
    /// <summary>
    /// Scans the specified folder for log files (legacy synchronous method).
    /// </summary>
    public IEnumerable<LogFileEntry> ScanFolder(string folderPath)
    {
        foreach (var entry in GetLogsAsync(folderPath).ToBlockingEnumerable())
        {
            yield return new LogFileEntry
            {
                FilePath = entry.SourceId,
                FileName = entry.Name,
                Content = entry.Content
            };
        }
    }
}

/// <summary>
/// Legacy log entry type. Use <see cref="LogEntry"/> instead.
/// </summary>
[Obsolete("Use LogEntry instead")]
public record LogFileEntry
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Content { get; init; }
}
