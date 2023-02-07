namespace custom_metrics_emitter;

using Azure.Identity;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger = default!;
    private readonly EmitterConfig _cfg = default!;
    private readonly EventHubEmitter _ehEmitter = default!;

    public Worker(ILogger<Worker> logger, IConfiguration configuration)
    {
        try
        {
            _logger = logger;

            _cfg = new(
                EventHubNamespace: configuration.Require("EventHubNamespace"),
                EventHubName: configuration.Require("EventHubName"),
                ConsumerGroup: configuration.Optional("ConsumerGroup"),
                CheckpointAccountName: configuration.Require("CheckpointAccountName"),
                CheckpointContainerName: configuration.Require("CheckpointContainerName"),
                Region: configuration.Require("Region"),
                TenantId: configuration.Require("TenantId"),
                SubscriptionId: configuration.Require("SubscriptionId"),
                ResourceGroup: configuration.Require("ResourceGroup"),
                ManagedIdentityClientId: configuration.Optional("ManagedIdentityClientId"),
                CustomMetricInterval: configuration.GetIntOrDefault("CustomMetricInterval", defaulT: 10_000));

            var defaultCredential = string.IsNullOrEmpty(_cfg.ManagedIdentityClientId)
                ? new DefaultAzureCredential()
                : new DefaultAzureCredential(options: new() { ManagedIdentityClientId = _cfg.ManagedIdentityClientId });

            _ehEmitter = new(_logger, _cfg, defaultCredential);
        }
        catch(Exception ex)
        {
            logger.LogError("{error}", ex.ToString());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.UtcNow);
                var res = await _ehEmitter.ReadFromBlobStorageAndPublishToAzureMonitorAsync(cancellationToken);

                if (res.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Send Custom Metric end with status: {status}", res.StatusCode);
                }
                else
                {
                    _logger.LogError("Error sending custom event with status: {status}", res.StatusCode);
                }

                await Task.Delay(_cfg.CustomMetricInterval, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("{error}", ex.ToString());
        }
    }
}

/// <summary>
/// A helper class to make configuration parsing more fluent.
/// </summary>
internal static class IConfigurationExtensions
{
    internal static int GetIntOrDefault(this IConfiguration cfg, string name, int defaulT) =>
        !string.IsNullOrEmpty(cfg.GetValue<string>(name)) && int.TryParse(cfg.GetValue<string>(name), out int value) ? value : defaulT;

    internal static string Optional(this IConfiguration cfg, string name) =>
        cfg.GetValue<string>(name) ?? string.Empty;

    internal static string Require(this IConfiguration cfg, string name)
    {
        var val = cfg.Optional(name);
        if (string.IsNullOrEmpty(val))
        {
            throw new ArgumentException($"Configuration error, missing key {name}", nameof(cfg));
        }
        return val;
    }
}