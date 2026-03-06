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

var defaultLogsFolder = configuration["LogAnalysis:LogsFolder"] ?? "./logs";
var logSourceType = configuration["LogAnalysis:Source"] ?? "FileSystem";
var minLevel = configuration["LogAnalysis:MinLevel"] ?? "Error";

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
    logSource = new FileSystemLogSource();
    location = logsFolder;
    Console.WriteLine($"Scanning logs in: {logsFolder}");
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

var systemPrompt = $"""
    You are a log analysis assistant. Your task is to:
    1. Analyze the provided log entries for issues at "{minLevel}" level and above
    2. For each distinct issue found, create a GitHub issue in the repository: {targetRepo}
    3. Before creating an issue, check if a similar issue already exists to avoid duplicates
    
    Severity levels (lowest to highest): Trace, Debug, Information, Warning, Error, Critical/Fatal.
    Only report entries at "{minLevel}" level or above. Ignore anything below that threshold.
    Log format may vary, but look for common patterns like severity keywords, "exception", stack traces, or other indicators of problems.
    Focus on actionable insights that can help developers identify and fix issues.

    When creating issues:
    - Use a clear, descriptive title summarizing the error
    - Include the error message, stack trace (if available), and file name in the body
    - Add the label "bug" if possible
    - Group related errors into a single issue
    
    Repository: {targetRepo}
    """;

// Create session with GitHub MCP server
var model = configuration["GitHub:Model"] ?? "gpt-4o";
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = model,
    SystemMessage = new SystemMessageConfig
    {
        Mode = SystemMessageMode.Append,
        Content = systemPrompt
    },
    McpServers = new Dictionary<string, object>
    {
        ["github"] = new McpRemoteServerConfig
        {
            Type = "http",
            Url = "https://api.githubcopilot.com/mcp/",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {githubToken}"
            },
            Tools = ["*"]
        }
    },
    OnPermissionRequest = PermissionHandler.ApproveAll
});

Console.WriteLine("\nAnalyzing logs and creating issues...\n");

var issuesCreated = 0;

// Process log entries one at a time as they arrive
try
{
    await foreach (var entry in logSource.GetLogsAsync(location, cts.Token))
    {
        Console.WriteLine($"[{logSource.SourceName}] Processing: {entry.Name}");

        var userPrompt = $"""
            Please analyze the following log entry for errors and create GitHub issues for any problems found:
            
            ### Source: {entry.Name} ({entry.SourceId})
            ```
            {entry.Content}
            ```
            """;

        var done = new TaskCompletionSource();
        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    Console.WriteLine(msg.Data.Content);
                    break;
                case ToolExecutionStartEvent toolStart:
                    var toolName = toolStart.Data.ToolName ?? "";
                    if (toolName.Contains("issue", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  [Tool] {toolName}");
                        if (toolName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                            toolName.Contains("write", StringComparison.OrdinalIgnoreCase))
                        {
                            issuesCreated++;
                        }
                    }
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
                case SessionErrorEvent error:
                    Console.Error.WriteLine($"Error: {error.Data.Message}");
                    done.TrySetException(new Exception(error.Data.Message));
                    break;
            }
        });

        await session.SendAsync(new MessageOptions
        {
            Prompt = userPrompt
        });

        await done.Task;
    }
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
