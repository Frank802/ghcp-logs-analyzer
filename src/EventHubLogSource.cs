using System.Runtime.CompilerServices;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;

namespace GhcpLogsAnalyzer;

/// <summary>
/// Log source that streams events from an Azure Event Hub using DefaultAzureCredential.
/// </summary>
public class EventHubLogSource : ILogSource
{
    private readonly string _fullyQualifiedNamespace;
    private readonly string _consumerGroup;
    private readonly bool _readFromEarliest;

    public EventHubLogSource(string fullyQualifiedNamespace, string consumerGroup = EventHubConsumerClient.DefaultConsumerGroupName, bool readFromEarliest = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedNamespace);
        _fullyQualifiedNamespace = fullyQualifiedNamespace;
        _consumerGroup = consumerGroup;
        _readFromEarliest = readFromEarliest;
    }

    /// <inheritdoc />
    public string SourceName => "EventHub";

    /// <inheritdoc />
    public async IAsyncEnumerable<LogEntry> GetLogsAsync(
        string location,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var consumer = new EventHubConsumerClient(
            _consumerGroup, _fullyQualifiedNamespace, location, new DefaultAzureCredential());

        var options = new ReadEventOptions
        {
            MaximumWaitTime = TimeSpan.FromSeconds(30)
        };

        await foreach (var partitionEvent in consumer.ReadEventsAsync(
            _readFromEarliest,
            options,
            cancellationToken))
        {
            // ReadEventsAsync may yield empty events when MaximumWaitTime elapses
            if (partitionEvent.Data is null)
                continue;

            var body = partitionEvent.Data.EventBody.ToString();
            if (string.IsNullOrWhiteSpace(body))
                continue;

            yield return new LogEntry
            {
                SourceId = $"{partitionEvent.Partition.PartitionId}-{partitionEvent.Data.SequenceNumber}",
                Name = $"event-{partitionEvent.Data.SequenceNumber}",
                Content = body
            };
        }
    }
}
