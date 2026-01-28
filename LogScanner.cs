namespace GhcpLogsAnalyzer;

/// <summary>
/// Service to scan a folder for log files and read their contents.
/// </summary>
public class LogScanner
{
    private static readonly string[] SupportedExtensions = [".log", ".txt"];

    /// <summary>
    /// Scans the specified folder for log files.
    /// </summary>
    /// <param name="folderPath">The folder path to scan.</param>
    /// <returns>A collection of log file entries with their contents.</returns>
    public IEnumerable<LogFileEntry> ScanFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Log folder not found: {folderPath}");
        }

        var logFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        foreach (var filePath in logFiles)
        {
            yield return new LogFileEntry
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Content = File.ReadAllText(filePath)
            };
        }
    }
}

/// <summary>
/// Represents a log file with its content.
/// </summary>
public record LogFileEntry
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Content { get; init; }
}
