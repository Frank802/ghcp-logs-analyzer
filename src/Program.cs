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
var model = configuration["GitHub:Model"] ?? "claude-sonnet-4.5";

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

// Determine log source
var logSourceType = configuration["LogSource"] ?? "FileSystem";
ILogSource logSource;
string logLocation;

if (string.Equals(logSourceType, "Kubernetes", StringComparison.OrdinalIgnoreCase))
{
    var sinceSeconds = int.TryParse(configuration["Kubernetes:SinceSeconds"], out var s) ? s : 300;
    var tailLines = int.TryParse(configuration["Kubernetes:TailLines"], out var t) ? t : 1000;
    logSource = new KubernetesLogSource(sinceSeconds, tailLines);
    logLocation = configuration["Kubernetes:LabelSelector"] ?? "";
    Console.WriteLine("Log source: Kubernetes (pod logs)");
}
else
{
    logSource = new FileSystemLogSource();
    logLocation = logsFolder;
    Console.WriteLine($"Log source: FileSystem — scanning: {logsFolder}");
}

List<LogEntry> logEntries;

try
{
    logEntries = await logSource.GetLogsAsync(logLocation).ToListAsync();
}
catch (DirectoryNotFoundException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (k8s.Autorest.HttpOperationException ex)
{
    Console.Error.WriteLine($"Kubernetes API error: {ex.Response.StatusCode} — {ex.Message}");
    return 1;
}

if (logEntries.Count == 0)
{
    Console.WriteLine("No log entries found.");
    return 0;
}

Console.WriteLine($"Found {logEntries.Count} log source(s)");

// Initialize Copilot client
await using var client = new CopilotClient( new CopilotClientOptions
{
    CliArgs = new[] {"--yolo", "--enable-all-github-mcp-tools"}
});
await client.StartAsync();

// Build the analysis prompt with all log contents
var logContents = string.Join("\n\n---\n\n", logEntries.Select(l =>
    $"### File: {l.Name}\n```\n{l.Content}\n```"));

var systemPrompt = $"""
    You are a log analysis assistant. Your task is to:
    1. Analyze the provided log files for errors, exceptions, and critical issues
    2. For each distinct error found, create a GitHub issue in the repository: {targetRepo}
    3. Before creating an issue, check if a similar issue already exists to avoid duplicates
    
    When creating issues:
    - Use a clear, descriptive title summarizing the error
    - Include the error message, stack trace (if available), and file name in the body
    - Add the label "bug" if possible
    - Group related errors into a single issue
    - Once the issue is created, assign it to Copilot
    
    Repository: {targetRepo}
    """;

var userPrompt = $"""
    Please analyze the following log files for errors and create GitHub issues for any problems found:
    
    {logContents}
    """;

// Create session with GitHub MCP server
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
    }
});

Console.WriteLine("\nAnalyzing logs and creating issues...\n");

// Track completion
var done = new TaskCompletionSource();
var issuesCreated = 0;

// Subscribe to session events
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

// Send the analysis request
await session.SendAsync(new MessageOptions
{
    Prompt = userPrompt
});

// Wait for completion
await done.Task;

Console.WriteLine($"\nDone! Created {issuesCreated} issue(s).");
return 0;
