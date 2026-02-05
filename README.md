# GitHub Copilot Logs Analyzer

A .NET 10 console application that analyzes log files for errors and automatically creates GitHub issues using the GitHub Copilot SDK and GitHub MCP Server.

## Features

- Scans a folder for `.log` and `.txt` files
- Uses GitHub Copilot SDK with AI-powered analysis to identify errors, exceptions, and critical issues
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
    "TargetRepository": "owner/repo"
  },
  "LogAnalysis": {
    "LogsFolder": "./logs",
    "SupportedExtensions": [ ".log", ".txt" ]
  }
}
```

### Option 2: Environment Variable

```powershell
$env:GITHUB_TOKEN = "ghp_your_personal_access_token"
```

## Usage

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

1. **Scan**: Reads all `.log` and `.txt` files from the specified folder
2. **Analyze**: Sends log contents to GitHub Copilot SDK with a system prompt for error analysis
3. **Create Issues**: Uses the GitHub MCP Server's `create_issue` tool to create issues for each distinct error found
4. **Deduplicate**: AI checks for existing similar issues before creating new ones

## Project Structure

```
ghcp-logs-analyzer/
├── src/
│   ├── Program.cs              # Main entry point with Copilot SDK integration
│   ├── LogScanner.cs           # Log file enumeration service
│   ├── appsettings.json        # Default configuration template
│   ├── appsettings.Local.json  # Local secrets (git-ignored)
│   ├── Dockerfile              # Docker container definition
│   ├── .dockerignore           # Docker build exclusions
│   └── GhcpLogsAnalyzer.csproj # Project file
├── sample-logs/                # Sample log files for testing
└── README.md
```

## License

MIT
