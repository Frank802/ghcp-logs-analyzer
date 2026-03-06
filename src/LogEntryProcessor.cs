using GitHub.Copilot.SDK;

namespace GhcpLogsAnalyzer;

/// <summary>
/// Processes a single log entry by creating a dedicated Copilot session,
/// analyzing the log content, and creating GitHub issues for any problems found.
/// </summary>
public class LogEntryProcessor
{
    private readonly CopilotClient _client;
    private readonly string _targetRepo;
    private readonly string _minLevel;
    private readonly string _githubToken;
    private readonly string _model;

    public LogEntryProcessor(CopilotClient client, string targetRepo, string minLevel, string githubToken, string model)
    {
        _client = client;
        _targetRepo = targetRepo;
        _minLevel = minLevel;
        _githubToken = githubToken;
        _model = model;
    }

    /// <summary>
    /// Creates a dedicated Copilot session, sends the log entry for analysis,
    /// and returns the number of issues created.
    /// </summary>
    public async Task<int> ProcessAsync(LogEntry entry, string sourceName, CancellationToken cancellationToken = default)
    {
        var prefix = $"[{entry.Name}]";
        Console.WriteLine($"{prefix} Starting analysis...");

        var systemPrompt = $"""
            You are a log analysis assistant. Your task is to:
            1. Analyze the provided log entries for issues at "{_minLevel}" level and above
            2. For each distinct issue found, create a GitHub issue in the repository: {_targetRepo}
            3. Before creating an issue, check if a similar issue already exists to avoid duplicates - NEVER SKIP THIS STEP
            
            Severity levels (lowest to highest): Trace, Debug, Information, Warning, Error, Critical/Fatal.
            Only report entries at "{_minLevel}" level or above. Ignore anything below that threshold.
            Log format may vary, but look for common patterns like severity keywords, "exception", stack traces, or other indicators of problems.
            Focus on actionable insights that can help developers identify and fix issues.

            When creating issues:
            - Use a clear, descriptive title summarizing the error
            - Include the error message, stack trace (if available), and file name in the body
            - Add the label "bug" and "log-analyzer" if possible
            - Group related errors into a single issue
            
            Repository: {_targetRepo}
            """;

        await using var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = _model,
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
                        ["Authorization"] = $"Bearer {_githubToken}"
                    },
                    Tools = ["*"]
                }
            },
            OnPermissionRequest = PermissionHandler.ApproveAll
        });

        var userPrompt = $"""
            Please analyze the following log entry and identify any issues that meet the severity threshold of "{_minLevel}" or above. 
            Create GitHub issues for any distinct problems found, ensuring to check for duplicates before creating new ones.
            
            ### Source: {entry.Name} ({entry.SourceId})
            ```
            {entry.Content}
            ```
            """;

        var issuesCreated = 0;
        var done = new TaskCompletionSource();

        using var ctReg = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));

        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    Console.WriteLine($"{prefix} {msg.Data.Content}");
                    break;
                case ToolExecutionStartEvent toolStart:
                    var toolName = toolStart.Data.ToolName ?? "";
                    if (toolName.Contains("issue", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"{prefix}   [Tool] {toolName}");
                        if (toolName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                            toolName.Contains("write", StringComparison.OrdinalIgnoreCase))
                        {
                            Interlocked.Increment(ref issuesCreated);
                        }
                    }
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
                case SessionErrorEvent error:
                    Console.Error.WriteLine($"{prefix} Error: {error.Data.Message}");
                    done.TrySetException(new Exception(error.Data.Message));
                    break;
            }
        });

        await session.SendAsync(new MessageOptions
        {
            Prompt = userPrompt
        });

        await done.Task;

        Console.WriteLine($"{prefix} Done — {issuesCreated} issue(s) created.");
        return issuesCreated;
    }
}
