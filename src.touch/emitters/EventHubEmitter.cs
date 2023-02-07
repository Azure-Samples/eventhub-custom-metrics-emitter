namespace custom_metrics_emitter;

using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using custom_metrics_emitter.emitters;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using System.Collections.Concurrent;

internal record LagInformation(string ConsumerName, string PartitionId, long Lag);

public class EventHubEmitter
{
    private const string LAG_METRIC_NAME = "Lag";
    private const string EVENT_HUB_CUSTOM_METRIC_NAMESPACE = "Event Hub custom metrics";

    // Implementation details from the EventHub .NET SDK
    private const string SEQUENCE_NUMBER = "sequenceNumber";
    private const string OFFSET_KEY = "offset";
    private readonly string _prefix;
    private string CheckpointBlobName(string consumerGroup, string partitionId) => $"{_prefix}/{consumerGroup.ToLowerInvariant()}/checkpoint/{partitionId}";

    private const string SERVICE_BUS_HOST_SUFFIX = ".servicebus.windows.net";
    private const string STORAGE_HOST_SUFFIX = ".blob.core.windows.net";

    private readonly ILogger<Worker> _logger;
    private readonly EmitterConfig _cfg;
    private readonly string _eventhubresourceId;

    private readonly EmitterHelper _emitter;
    private readonly BlobContainerClient _checkpointContainerClient = default!;
    private readonly Dictionary<string, ConsumerClientInfo> _eventhubConsumerClientsInfo = new();
    private readonly string[] _consumerGroups = default!;    

    public EventHubEmitter(ILogger<Worker> logger, EmitterConfig config, DefaultAzureCredential defaultCredential)
    {
        
        (_logger, _cfg) = (logger, config);

        _emitter = new EmitterHelper(_logger, defaultCredential);

        if (string.IsNullOrEmpty(config.ConsumerGroup))
        { 
            _consumerGroups = _emitter.GetAllConsumerGroup(_cfg.EventHubNamespace, _cfg.EventHubName);
        }
        else
        {
            _consumerGroups = config.ConsumerGroup.Split(';');             
        }

        _eventhubresourceId = $"/subscriptions/{_cfg.SubscriptionId}/resourceGroups/{_cfg.ResourceGroup}/providers/Microsoft.EventHub/namespaces/{_cfg.EventHubNamespace}";
        _prefix = $"{_cfg.EventHubNamespace.ToLowerInvariant()}{SERVICE_BUS_HOST_SUFFIX}/{_cfg.EventHubName.ToLowerInvariant()}";

        
        _checkpointContainerClient = new BlobContainerClient(
            blobContainerUri: new($"https://{_cfg.CheckpointAccountName}{STORAGE_HOST_SUFFIX}/{_cfg.CheckpointContainerName}"),
            credential: defaultCredential);

        //init eventhubConsumerClients per consumer group
        foreach (string cGroup in _consumerGroups)
        {
            var client = new EventHubConsumerClient(
                consumerGroup: cGroup,
                fullyQualifiedNamespace: $"{_cfg.EventHubNamespace.ToLowerInvariant()}{SERVICE_BUS_HOST_SUFFIX}",
                eventHubName: _cfg.EventHubName,
                credential: defaultCredential);

            var partitions = client.GetPartitionIdsAsync().Result;

            _eventhubConsumerClientsInfo.TryAdd(cGroup,
                new(consumerClient: client, partitionIds: partitions));                           
        }   
    }    

    public async Task<HttpResponseMessage> ReadFromBlobStorageAndPublishToAzureMonitorAsync(CancellationToken cancellationToken = default)
    {
        var totalLag = await GetLagAsync(cancellationToken);

        var emitterdata = new EmitterSchema(
            time: DateTime.UtcNow,
            data: new CustomMetricData(
                baseData: new CustomMetricBaseData(
                    metric: LAG_METRIC_NAME,
                    Namespace: EVENT_HUB_CUSTOM_METRIC_NAMESPACE,
                    dimNames: new[] { "EventHubName", "ConsumerGroup", "PartitionId" },
                    series: totalLag.Select((lagInfo, idx) =>
                        new CustomMetricBaseDataSeriesItem(
                            dimValues: new[] { _cfg.EventHubName, lagInfo.ConsumerName, lagInfo.PartitionId },
                            min: null, max: null,
                            count: idx + 1,
                            sum: lagInfo.Lag)))));

        return await _emitter.SendCustomMetric(
            region: _cfg.Region,
            resourceId: _eventhubresourceId,
            metricToSend: emitterdata,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<LagInformation>> GetLagAsync(CancellationToken cancellationToken = default)
    {
        // Query all partitions in parallel
        var tasks = from consumer in _consumerGroups
                    from id in _eventhubConsumerClientsInfo[consumer]._partitionIds
                    select new { consumerGroup = consumer, partitionId = id, Task = LagInPartition(consumer, id, cancellationToken) };

        await Task.WhenAll(tasks.Select(s => s.Task));        

        return tasks
            .Select(x => new LagInformation(x.consumerGroup, x.partitionId, x.Task.Result))
            .OrderBy(x => x.PartitionId);
    }

    private async Task<long> LagInPartition(string consumerGroup,
        string partitionId, CancellationToken cancellationToken = default)
    {
        long retVal = 0;
        try
        {
            var partitionInfo = await _eventhubConsumerClientsInfo[consumerGroup]._consumerClient.GetPartitionPropertiesAsync(
                partitionId,
                cancellationToken);           
            // if partitionInfo.LastEnqueuedOffset = -1, that means event hub partition is empty
            if ((partitionInfo != null) && (partitionInfo.LastEnqueuedOffset == -1))
            {
                _logger.LogInformation("LagInPartition Empty partition");
            }
            else
            {
                string checkpointName = CheckpointBlobName(consumerGroup, partitionId);
                _logger.LogInformation("LagInPartition Checkpoint GetProperties: {name}", checkpointName);

                BlobProperties properties = await _checkpointContainerClient
                    .GetBlobClient(checkpointName)
                    .GetPropertiesAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                string strSeqNum, strOffset;
                if (properties.Metadata.TryGetValue(SEQUENCE_NUMBER, out strSeqNum!) &&
                    properties.Metadata.TryGetValue(OFFSET_KEY, out strOffset!))
                {
                    if (long.TryParse(strSeqNum, out long seqNum))
                    {
                        _logger.LogInformation("LagInPartition Start: {checkpoint name} seq={seqNum} offset={offset}", checkpointName, seqNum, strOffset);

                        // If checkpoint.Offset is empty that means no messages has been processed from an event hub partition
                        // And since partitionInfo.LastSequenceNumber = 0 for the very first message hence
                        // total unprocessed message will be partitionInfo.LastSequenceNumber + 1
                        if (string.IsNullOrEmpty(strOffset) == true)
                        {
                            retVal = partitionInfo!.LastEnqueuedSequenceNumber + 1;
                        }
                        else
                        {
                            if (partitionInfo!.LastEnqueuedSequenceNumber >= seqNum)
                            {
                                retVal = partitionInfo.LastEnqueuedSequenceNumber - seqNum;
                            }
                            else
                            {
                                // Partition is a circular buffer, so it is possible that
                                // partitionInfo.LastSequenceNumber < blob checkpoint's SequenceNumber
                                retVal = (long.MaxValue - partitionInfo.LastEnqueuedSequenceNumber) + seqNum;

                                if (retVal < 0)
                                    retVal = 0;
                            }
                        }
                        _logger.LogInformation("LagInPartition End: {checkpoint name} seq={seqNum} offset={offset} lag={lag}", checkpointName, seqNum, strOffset, retVal);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("LagInPartition Error: {error}", ex.ToString());
        }
        return retVal;
    }

    private class ConsumerClientInfo
    {
        public EventHubConsumerClient _consumerClient;
        public string[] _partitionIds;

        public ConsumerClientInfo(EventHubConsumerClient consumerClient, string[] partitionIds)
        {
            _consumerClient = consumerClient;
            _partitionIds = partitionIds;
        }
    }
}