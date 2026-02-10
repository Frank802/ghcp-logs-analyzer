# Copilot Instructions — ghcp-logs-analyzer

## Architecture

This is a .NET 10 console application that analyzes log files for errors using the **GitHub Copilot SDK** and automatically creates GitHub issues via the **GitHub MCP Server**. It is a single-project solution (`src/GhcpLogsAnalyzer.csproj`).

### Data Flow

1. `ILogSource` (abstraction) yields `LogEntry` records from a source:
   - `FileSystemLogSource` reads `.log`/`.txt` files from disk.
   - `KubernetesLogSource` reads live container logs from running pods in the same K8s namespace via the Kubernetes API.
2. `Program.cs` (top-level statements) selects the log source based on the `LogSource` config key, collects all entries, composes a system+user prompt with the log contents, then sends them to a `CopilotClient` session.
3. The session is configured with the GitHub MCP remote server (`https://api.githubcopilot.com/mcp/`) so the model can call `create_issue` / search existing issues to deduplicate.
4. Events (`AssistantMessageEvent`, `ToolExecutionStartEvent`, `SessionIdleEvent`, `SessionErrorEvent`) are consumed via `session.On(...)` to stream output and track issue creation.

### Key Dependencies

- `GitHub.Copilot.SDK` — the Copilot client; requires the `copilot` CLI binary in PATH at runtime.
- `KubernetesClient` — official Kubernetes .NET client; used by `KubernetesLogSource` for pod discovery and log retrieval.
- `Microsoft.Extensions.Configuration.*` — layered config (JSON → env vars → CLI args).

### Kubernetes Deployment Model

The analyzer assumes a **one namespace = one app = one repo** model. When deployed to K8s, it runs in the same namespace as the target application and auto-detects the namespace from the service account mount. It discovers running pods, reads their container logs via the K8s API, and excludes its own pod from analysis. RBAC manifests are in the `k8s/` directory.

## Build & Run

```powershell
# Build (from repo root or src/)
dotnet build src/GhcpLogsAnalyzer.csproj

# Run (from src/)
cd src
dotnet run                              # uses appsettings config
dotnet run -- owner/repo ../sample-logs # CLI overrides
```

Docker: `docker build -t ghcp-logs-analyzer src/` — the runtime image installs Node.js + `@github/copilot` CLI.

## Configuration Priority

1. CLI args (`args[0]` = repo, `args[1]` = logs folder)
2. `appsettings.Local.json` (git-ignored, may contain real tokens)
3. `appsettings.json` (committed template with placeholder values)
4. Environment variables (`GITHUB_TOKEN`, `GITHUB_TARGET_REPOSITORY`)

### Config Keys

| Key | Default | Description |
|-----|---------|-------------|
| `LogSource` | `FileSystem` | Log source to use (`FileSystem` or `Kubernetes`) |
| `Kubernetes:LabelSelector` | _(empty)_ | K8s label selector to filter pods |
| `Kubernetes:SinceSeconds` | `300` | Only read logs from the last N seconds |
| `Kubernetes:TailLines` | `1000` | Max log lines per container |

**Never commit real tokens.** `appsettings.Local.json` is for local secrets only.

## Project Conventions

- **Top-level statements** in `Program.cs` — no `Main` method or `Startup` class; the file is the entire entry point.
- **`ILogSource` / `LogEntry` pattern** — new log sources (e.g., Azure Blob, HTTP) should implement `ILogSource` and return `IAsyncEnumerable<LogEntry>`. See `FileSystemLogSource` and `KubernetesLogSource` as reference implementations.
- **Legacy types are marked `[Obsolete]`** — `LogScanner` and `LogFileEntry` in `FileSystemLogSource.cs` exist for backward compatibility; do not use them in new code.
- **Records with `required` properties** — `LogEntry` uses `required init` props (`SourceId`, `Name`, `Content`).
- C# features: implicit usings, nullable enabled, file-scoped namespaces, collection expressions (`[".log", ".txt"]`).

## Adding a New Log Source

1. Create a class implementing `ILogSource` in `src/`.
2. Return `IAsyncEnumerable<LogEntry>` from `GetLogsAsync`.
3. Wire it up in `Program.cs` in place of (or alongside) `FileSystemLogSource`.

## Testing

There is no test project yet. The `sample-logs/` directory contains sample log files (`app.log`) used for manual/integration testing via `dotnet run`.

## Things to Watch Out For

- The Copilot SDK communicates with a local `copilot` CLI process (`CopilotClient` → child process). The CLI must be installed and authenticated before running.
- `session.On(...)` uses a callback-based event model — all AI responses and tool calls flow through event discriminators (`switch` on event type).
- Issue-creation count is tracked heuristically by inspecting tool names for "create"/"write" + "issue" substrings; it is not an exact count from the API.
