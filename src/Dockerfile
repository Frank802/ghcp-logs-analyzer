# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY GhcpLogsAnalyzer.csproj .
RUN dotnet restore

# Copy source code and build
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Install Node.js and GitHub Copilot CLI (the SDK expects 'copilot' binary in PATH)
RUN apt-get update && apt-get install -y \
    curl \
    ca-certificates \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs \
    && npm install -g @github/copilot \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=build /app/publish .

# Copy configuration files
COPY appsettings.json .

# Create logs directory mount point
RUN mkdir -p /logs

# Set environment variables
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Entry point
ENTRYPOINT ["dotnet", "GhcpLogsAnalyzer.dll"]
