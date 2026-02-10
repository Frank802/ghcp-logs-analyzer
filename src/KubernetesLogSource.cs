using System.Net;
using System.Runtime.CompilerServices;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace GhcpLogsAnalyzer;

/// <summary>
/// Log source that reads container logs from running pods in a Kubernetes namespace.
/// Designed for per-app namespace troubleshooting: deploy the analyzer into the same
/// namespace as the application under investigation. The namespace is auto-detected
/// from the pod's service account mount (in-cluster) or from kubeconfig (local dev).
/// </summary>
public class KubernetesLogSource : ILogSource, IDisposable
{
    private readonly IKubernetes _client;
    private readonly string _namespace;
    private readonly int _sinceSeconds;
    private readonly int _tailLines;
    private readonly string? _selfPodName;

    /// <summary>
    /// Creates a new Kubernetes log source.
    /// </summary>
    /// <param name="sinceSeconds">Only return logs newer than this many seconds (default: 300 = 5 minutes).</param>
    /// <param name="tailLines">Maximum number of log lines to retrieve per container (default: 1000).</param>
    /// <param name="namespaceOverride">
    /// Override the namespace. When null (default), the namespace is auto-detected:
    /// in-cluster from the service account mount, or from kubeconfig for local dev.
    /// Typically left null — the analyzer runs in the same namespace as the target app.
    /// </param>
    public KubernetesLogSource(int sinceSeconds = 300, int tailLines = 1000, string? namespaceOverride = null)
    {
        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildDefaultConfig();

        _client = new Kubernetes(config);
        _namespace = namespaceOverride ?? config.Namespace ?? "default";
        _sinceSeconds = sinceSeconds;
        _tailLines = tailLines;

        // Used to exclude the analyzer's own pod from log collection.
        // In Kubernetes, the HOSTNAME env var equals the pod name.
        _selfPodName = Environment.GetEnvironmentVariable("HOSTNAME");
    }

    /// <inheritdoc />
    public string SourceName => "Kubernetes";

    /// <inheritdoc />
    public async IAsyncEnumerable<LogEntry> GetLogsAsync(
        string location,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // location = Kubernetes label selector (e.g. "app=myservice") or empty/null for all pods.
        // Since each app has its own namespace, an empty selector (all pods) is the typical case.
        var labelSelector = string.IsNullOrWhiteSpace(location) ? null : location;

        Console.WriteLine($"Listing pods in namespace '{_namespace}'"
            + (labelSelector is not null ? $" with selector '{labelSelector}'" : "") + "...");

        var podList = await _client.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: _namespace,
            labelSelector: labelSelector,
            cancellationToken: cancellationToken);

        var pods = podList.Items
            .Where(p => p.Status?.Phase == "Running")
            .Where(p => !string.Equals(p.Metadata.Name, _selfPodName, StringComparison.Ordinal))
            .ToList();

        if (pods.Count == 0)
        {
            Console.WriteLine("No running pods found.");
            yield break;
        }

        Console.WriteLine($"Found {pods.Count} running pod(s). Reading logs (last {_sinceSeconds}s, tail {_tailLines} lines)...");

        foreach (var pod in pods)
        {
            var containers = pod.Spec.Containers ?? [];

            foreach (var container in containers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string content;
                try
                {
                    var stream = _client.CoreV1.ReadNamespacedPodLog(
                        name: pod.Metadata.Name,
                        namespaceParameter: _namespace,
                        container: container.Name,
                        sinceSeconds: _sinceSeconds,
                        tailLines: _tailLines);

                    using var reader = new StreamReader(stream);
                    content = await reader.ReadToEndAsync(cancellationToken);
                }
                catch (HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    // Container may not have started yet, has been evicted, or has no logs.
                    continue;
                }

                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var podContainer = containers.Count > 1
                    ? $"{pod.Metadata.Name}/{container.Name}"
                    : pod.Metadata.Name;

                yield return new LogEntry
                {
                    SourceId = $"k8s://{_namespace}/{pod.Metadata.Name}/{container.Name}",
                    Name = podContainer,
                    Content = content
                };
            }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
