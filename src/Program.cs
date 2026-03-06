using GitHub.Copilot.SDK;
using GhcpLogsAnalyzer;
using Microsoft.Extensions.Configuration;

// Build configuration from appsettings.json and environment variables
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var githubToken = configuration["GitHub:Token"];
if (string.IsNullOrEmpty(githubToken) || githubToken == "YOUR_GITHUB_TOKEN")
{
    githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
}

var defaultRepo = configuration["GitHub:TargetRepository"];
if(string.IsNullOrEmpty(defaultRepo) || defaultRepo == "owner/repo")
{
    defaultRepo = Environment.GetEnvironmentVariable("GITHUB_TARGET_REPOSITORY");
}

var defaultLogsFolder = configuration["FileSystem:LogsFolder"] ?? configuration["LogAnalysis:LogsFolder"] ?? "./logs";
var logSourceType = configuration["LogAnalysis:Source"] ?? "FileSystem";
var minLevel = configuration["LogAnalysis:MinLevel"] ?? "Error";
var maxConcurrency = int.TryParse(configuration["LogAnalysis:MaxConcurrency"], out var mc) && mc > 0 ? mc : 5;

// Parse command line arguments (override config if provided)
string targetRepo;
string logsFolder;

if (args.Length >= 2)
{
    targetRepo = args[0];
    logsFolder = args[1];
}
else if (args.Length == 1)
{
    targetRepo = args[0];
    logsFolder = defaultLogsFolder;
}
else
{
    targetRepo = defaultRepo ?? "";
    logsFolder = defaultLogsFolder;
}

// Validate required settings
if (string.IsNullOrEmpty(targetRepo))
{
    Console.WriteLine("Usage: GhcpLogsAnalyzer <owner/repo> [logs-folder]");
    Console.WriteLine("Example: GhcpLogsAnalyzer octocat/my-app ./logs");
    Console.WriteLine();
    Console.WriteLine("Configuration sources (in priority order):");
    Console.WriteLine("  1. Command line arguments");
    Console.WriteLine("  2. appsettings.json");
    Console.WriteLine("  3. Environment variables (GITHUB_TOKEN, GITHUB_TARGET_REPOSITORY)");
    return 1;
}

if (string.IsNullOrEmpty(githubToken))
{
    Console.Error.WriteLine("Error: GitHub token not configured.");
    Console.Error.WriteLine("Set it in appsettings.json (GitHub:Token) or GITHUB_TOKEN environment variable.");
    return 1;
}

// Build the log source and determine the location
ILogSource logSource;
string location;

Console.WriteLine("Starting GitHub Copilot Logs Analyzer...");
Console.WriteLine($"Log Source Type: {logSourceType}");
Console.WriteLine($"Minimum Log Level: {minLevel}");
Console.WriteLine($"GitHub Repository: {targetRepo}");

if (logSourceType.Equals("EventHub", StringComparison.OrdinalIgnoreCase))
{
    var fullyQualifiedNamespace = configuration["EventHub:FullyQualifiedNamespace"];
    var eventHubName = configuration["EventHub:EventHubName"];
    var consumerGroup = configuration["EventHub:ConsumerGroup"] ?? "$Default";
    var startFromEarliest = bool.TryParse(configuration["EventHub:StartFromEarliest"], out var earliest) && earliest;

    if (string.IsNullOrEmpty(fullyQualifiedNamespace))
    {
        Console.Error.WriteLine("Error: EventHub:FullyQualifiedNamespace is required when LogAnalysis:Source is EventHub.");
        return 1;
    }
    if (string.IsNullOrEmpty(eventHubName))
    {
        Console.Error.WriteLine("Error: EventHub:EventHubName is required when LogAnalysis:Source is EventHub.");
        return 1;
    }

    logSource = new EventHubLogSource(fullyQualifiedNamespace, consumerGroup, startFromEarliest);
    location = eventHubName;
    Console.WriteLine($"Listening for events on Event Hub: {fullyQualifiedNamespace}/{eventHubName} (consumer group: {consumerGroup})");
}
else
{
    var supportedExtensions = configuration["FileSystem:SupportedExtensions"] ?
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToArray() ?? new[] { ".log", ".txt" };
    logSource = new FileSystemLogSource(supportedExtensions);
    location = logsFolder;
    Console.WriteLine($"Scanning logs in: {logsFolder} (supported extensions: {string.Join(", ", supportedExtensions)})");
}

// Support graceful shutdown via Ctrl+C
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nShutting down...");
};

// Initialize Copilot client
await using var client = new CopilotClient();
await client.StartAsync();

var model = configuration["GitHub:Model"] ?? "gpt-4o";
var processor = new LogEntryProcessor(client, targetRepo, minLevel, githubToken, model);

Console.WriteLine("\nStarting log analysis with model: " + model);
Console.WriteLine($"\nAnalyzing logs and creating issues (max concurrency: {maxConcurrency})...\n");

var issuesCreated = 0;
var semaphore = new SemaphoreSlim(maxConcurrency);
var tasks = new List<Task>();

// Process log entries in parallel, each with a dedicated Copilot session
try
{
    await foreach (var entry in logSource.GetLogsAsync(location, cts.Token))
    {
        Console.WriteLine($"[{logSource.SourceName}] Queued: {entry.Name}");

        await semaphore.WaitAsync(cts.Token);

        var capturedEntry = entry;
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                var count = await processor.ProcessAsync(capturedEntry, logSource.SourceName, cts.Token);
                Interlocked.Add(ref issuesCreated, count);
            }
            finally
            {
                semaphore.Release();
            }
        }, cts.Token));
    }

    await Task.WhenAll(tasks);
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // Graceful shutdown via Ctrl+C
}
catch (DirectoryNotFoundException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

Console.WriteLine($"\nDone! Created {issuesCreated} issue(s).");
return 0;
