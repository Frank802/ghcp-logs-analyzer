# GitHub Copilot Logs Analyzer

A .NET 10 console application that analyzes log files for errors and automatically creates GitHub issues using the GitHub Copilot SDK and GitHub MCP Server.

## Features

- **File system source**: Scans a folder for `.log` and `.txt` files
- **Kubernetes source**: Reads live container logs from running pods in the same namespace — ideal for troubleshooting a running app on K8s
- Uses GitHub Copilot SDK with AI-powered analysis to identify errors, exceptions, and critical issues
- Configurable AI model (defaults to `claude-sonnet-4.5`)
- Automatically creates GitHub issues via the GitHub MCP Server
- Checks for duplicate issues before creating new ones
- Supports configuration via `appsettings.json`, environment variables, or command-line arguments

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli) installed and in PATH
- GitHub Copilot subscription
- GitHub Personal Access Token (PAT) with `repo` scope

## Installation

```bash
git clone https://github.com/Frank802/ghcp-logs-analyzer.git
cd ghcp-logs-analyzer/src
dotnet restore
```

## Configuration

### Option 1: appsettings.Local.json (Recommended)

Create `src/appsettings.Local.json` (git-ignored):

```json
{
  "GitHub": {
    "Token": "ghp_your_personal_access_token",
    "TargetRepository": "owner/repo",
    "Model": "claude-sonnet-4.5"
  },
  "LogSource": "FileSystem",
  "LogAnalysis": {
    "LogsFolder": "./logs",
    "SupportedExtensions": [ ".log", ".txt" ]
  },
  "Kubernetes": {
    "LabelSelector": "",
    "SinceSeconds": 300,
    "TailLines": 1000
  }
}
```

Set `LogSource` to `"Kubernetes"` to read live pod logs instead of files (see [Kubernetes Deployment](#kubernetes-deployment)).

### Option 2: Environment Variable

```powershell
$env:GITHUB_TOKEN = "ghp_your_personal_access_token"
```

## Usage

### File System Mode (default)

```bash
# Run from the src folder
cd src

# Using config file
dotnet run

# Override with command-line arguments
dotnet run -- <owner/repo> [logs-folder]

# Example
dotnet run -- Frank802/my-app ../sample-logs
```

### Kubernetes Mode

```bash
cd src

# Set via environment variables (PowerShell)
$env:LogSource = "Kubernetes"
dotnet run -- owner/repo

# Or with a label selector to filter pods
$env:Kubernetes__LabelSelector = "app=myservice"
dotnet run -- owner/repo
```

When running locally, `KubernetesLogSource` uses your kubeconfig. When deployed in-cluster, it auto-detects the namespace. See [Kubernetes Deployment](#kubernetes-deployment) for the full guide.

## Configuration Priority

1. Command-line arguments (highest)
2. `appsettings.Local.json`
3. `appsettings.json`
4. Environment variables (`GITHUB_TOKEN`, `GITHUB_TARGET_REPOSITORY`)

## Running with Docker

### Build the Image

```bash
cd src
docker build -t ghcp-logs-analyzer .
```

### Run the Container

```bash
docker run --rm \
  -v /path/to/your/logs:/logs \
  -v ~/.config/github-copilot:/root/.config/github-copilot:ro \
  -e GITHUB_TOKEN=ghp_your_personal_access_token \
  -e GITHUB_TARGET_REPOSITORY=owner/repo \
  ghcp-logs-analyzer
```

### Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `GITHUB_TOKEN` | Yes | GitHub PAT with `repo` scope |
| `GITHUB_TARGET_REPOSITORY` | Yes | Target repository (e.g., `owner/repo`) |
| `GitHub__Model` | No | AI model to use (default: `claude-sonnet-4.5`) |
| `LogSource` | No | `FileSystem` (default) or `Kubernetes` |
| `Kubernetes__LabelSelector` | No | K8s label selector to filter pods (e.g., `app=myservice`) |
| `Kubernetes__SinceSeconds` | No | Only read logs from the last N seconds (default: `300`) |
| `Kubernetes__TailLines` | No | Max log lines per container (default: `1000`) |

### Volume Mounts

| Host Path | Container Path | Purpose |
|-----------|----------------|---------|
| `/path/to/your/logs` | `/logs` | Log files to analyze |
| `~/.config/github-copilot` | `/root/.config/github-copilot` | Copilot CLI credentials (read-only) |

### Example with Command-Line Arguments

```bash
docker run --rm \
  -v /path/to/logs:/logs \
  -v ~/.config/github-copilot:/root/.config/github-copilot:ro \
  -e GITHUB_TOKEN=ghp_your_token \
  ghcp-logs-analyzer Frank802/my-app /logs
```

> **Note**: You must authenticate the GitHub Copilot CLI on your host machine first (`github-copilot auth`), then mount the credentials into the container.

## How It Works

1. **Discover**: Reads log files from disk, or discovers running pods in the Kubernetes namespace
2. **Collect**: Gathers log content from files or container stdout/stderr via the K8s API
3. **Analyze**: Sends log contents to GitHub Copilot SDK with a system prompt for error analysis
4. **Create Issues**: Uses the GitHub MCP Server's `create_issue` tool to create issues for each distinct error found
5. **Deduplicate**: AI checks for existing similar issues before creating new ones

## Log Sources

The analyzer supports pluggable log sources via the `LogSource` configuration key.

### File System (default)

Reads `.log` and `.txt` files from a local folder. Set `LogSource` to `FileSystem` (or omit it — it's the default).

### Kubernetes

Reads live container logs from running pods in the same Kubernetes namespace. Set `LogSource` to `Kubernetes`.

See [Kubernetes Deployment](#kubernetes-deployment) below for the full setup guide.

## Project Structure

```
ghcp-logs-analyzer/
├── src/
│   ├── Program.cs              # Main entry point with Copilot SDK integration
│   ├── ILogSource.cs           # Log source abstraction (ILogSource + LogEntry)
│   ├── FileSystemLogSource.cs  # File system log source implementation
│   ├── KubernetesLogSource.cs  # Kubernetes pod log source implementation
│   ├── appsettings.json        # Default configuration template
│   ├── appsettings.Local.json  # Local secrets (git-ignored)
│   ├── Dockerfile              # Docker container definition
│   └── GhcpLogsAnalyzer.csproj # Project file
├── k8s/
│   ├── rbac.yaml               # ServiceAccount, Role, and RoleBinding
│   └── job.yaml                # Job manifest and Secret template
├── sample-logs/                # Sample log files for testing
└── README.md
```

## Kubernetes Deployment

The analyzer can be deployed into a Kubernetes cluster to troubleshoot a running application by reading live pod logs.

### Deployment Model: One Namespace per App

This tool assumes each application (with its own repository) runs in a **dedicated Kubernetes namespace**. The analyzer is deployed into the **same namespace** as the application it troubleshoots:

```
Namespace: my-app-prod
├── my-app-deployment (the application)
├── my-app-db (database sidecar, etc.)
└── ghcp-logs-analyzer (this tool — reads logs from sibling pods)
```

The analyzer **auto-detects its namespace** from the Kubernetes service account mount — no namespace configuration is needed. It discovers all running pods in that namespace, reads their container logs via the Kubernetes API, and sends them to the Copilot AI for error analysis and GitHub issue creation.

The analyzer automatically **excludes itself** from log collection to avoid self-analysis.

### Prerequisites

- A Kubernetes cluster with the target application deployed
- `kubectl` configured to access the cluster
- A GitHub PAT with `repo` scope
- The analyzer container image built and available to the cluster

### Quick Start

1. **Build and push the container image:**

   ```bash
   cd src
   docker build -t ghcp-logs-analyzer:latest .
   # Push to your registry:
   # docker tag ghcp-logs-analyzer:latest <registry>/ghcp-logs-analyzer:latest
   # docker push <registry>/ghcp-logs-analyzer:latest
   ```

2. **Edit the manifests** in `k8s/`:

   - In `k8s/job.yaml`: set your GitHub token in the Secret and your `owner/repo` in the Job env vars.
   - Optionally update the image reference if using a private registry.

3. **Deploy into the target app's namespace:**

   ```bash
   # Apply RBAC (ServiceAccount + Role + RoleBinding)
   kubectl apply -f k8s/rbac.yaml -n <your-app-namespace>

   # Run the analyzer as a one-shot Job
   kubectl apply -f k8s/job.yaml -n <your-app-namespace>
   ```

4. **Check the results:**

   ```bash
   kubectl logs job/ghcp-logs-analyzer -n <your-app-namespace>
   ```

   The analyzer will list discovered pods, read their logs, analyze them with AI, and create GitHub issues for any errors found.

### RBAC

The analyzer needs minimal permissions — only read access to pods and their logs in its own namespace:

| Resource | Verbs | Purpose |
|----------|-------|---------|
| `pods` | `get`, `list` | Discover running pods and their containers |
| `pods/log` | `get` | Read container stdout/stderr logs |

These are namespace-scoped (via `Role` + `RoleBinding`), not cluster-wide.

### Configuration

When running in Kubernetes, set `LogSource` to `Kubernetes` via environment variable or config file.

| Setting | Env Var / Config Key | Default | Description |
|---------|---------------------|---------|-------------|
| Log source | `LogSource` | `FileSystem` | Set to `Kubernetes` to read pod logs |
| Label selector | `Kubernetes:LabelSelector` | _(empty = all pods)_ | Filter pods by label (e.g., `app=myservice`) |
| Time window | `Kubernetes:SinceSeconds` | `300` | Only read logs from the last N seconds |
| Line limit | `Kubernetes:TailLines` | `1000` | Max log lines per container |

Since each app has its own namespace, an empty label selector (all pods) is typically what you want. Use the selector to filter out infrastructure pods (e.g., sidecars) if needed.

### Local Development with Kubernetes

You can test the Kubernetes source locally if you have a kubeconfig pointing at a cluster:

```bash
cd src
# Set log source to Kubernetes and target a specific namespace
dotnet run -- owner/repo

# With environment variables:
$env:LogSource = "Kubernetes"
$env:Kubernetes__LabelSelector = "app=myservice"
dotnet run -- owner/repo
```

The `KubernetesLogSource` falls back to `BuildDefaultConfig()` (your local kubeconfig) when not running inside a cluster.

## License

MIT
