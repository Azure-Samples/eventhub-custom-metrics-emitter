namespace custom_metrics_emitter.emitters;

using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Amqp.Framing;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Xml.Linq;

public record struct AccessTokenAndExpiration(bool isExpired, string token);

public class EmitterHelper
{
    private static readonly HttpClient _httpClient = new();    
    private readonly ILogger<Worker> _logger;
    private readonly TokenStore _TokenStore;

    public EmitterHelper(ILogger<Worker> logger, DefaultAzureCredential defaultAzureCredential)
    {
        _logger = logger;
        _TokenStore = new TokenStore(
            defaultAzureCredential);
    }  

    public async Task<HttpResponseMessage> SendCustomMetric(
        string? region, string? resourceId, EmitterSchema metricToSend,
        CancellationToken cancellationToken = default)
    {
        if ((region != null) && (resourceId != null))
        {
            var record = await _TokenStore.RefreshAzureMonitorCredentialOnDemandAsync(cancellationToken);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", record.token);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            string uri = $"https://{region}.monitoring.azure.com{resourceId}/metrics";
            string jsonString = JsonSerializer.Serialize(metricToSend, _jsonOptions);

            StringContent content = new(
                content: jsonString,
                encoding: Encoding.UTF8,
                mediaType: "application/json");

            _logger.LogInformation("SendCustomMetric:{uri} with payload:{payload}", uri, jsonString);

            return await _httpClient.PostAsync(uri, content, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.LengthRequired);
    }

    public string[] GetAllConsumerGroup(string eventhubNamespace, string eventhub)
    {
        var ehRecord = _TokenStore.RefreshAzureEventHubCredentialOnDemand();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ehRecord.token);

        string uri = $"https://{eventhubNamespace}.servicebus.windows.net/{eventhub}/consumergroups?timeout=60&api-version=2014-01";

        _logger.LogInformation("GetAllConsumerGroup:{uri}", uri);
        var response = _httpClient.GetAsync(uri).Result.Content.ReadAsStringAsync().Result;        
        var doc = XDocument.Parse(response);
        var entries = from item in doc.Root!.
                      Descendants().
                      Where(i => i.Name.LocalName == "entry").
                      Descendants().
                      Where(j => j.Name.LocalName == "title")
                      select item.Value;

        return entries.ToArray<string>();
    }

    public ValueTask<AccessTokenAndExpiration> RefreshAzureEventHubCredentialOnDemandAsync(CancellationToken cancellationToken = default)
    {
        return _TokenStore.RefreshAzureEventHubCredentialOnDemandAsync(cancellationToken);
    }

    public ValueTask<AccessTokenAndExpiration> RefreshCredentialOnDemandAsync(string audience,
        CancellationToken cancellationToken = default)
    {
        return _TokenStore.RefreshCredentialOnDemand(audience, cancellationToken);
    }

    private static JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new() { WriteIndented = false };
        options.Converters.Add(new SortableDateTimeConverter());
        return options;
    }

    private class SortableDateTimeConverter : JsonConverter<DateTime>
    {
        private const string format = "s"; //SortableDateTimePattern yyyy'-'MM'-'dd'T'HH':'mm':'ss

        public override void Write(Utf8JsonWriter writer, DateTime date, JsonSerializerOptions options)
        {
            writer.WriteStringValue(date.ToUniversalTime().ToString(format));
        }
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return DateTime.ParseExact(reader.GetString()!, format, provider: null);
        }
    }

    private class TokenStore
    {
        private static readonly string MONITOR_SCOPE = "https://monitor.azure.com/.default";
        private static readonly string EVENTHUBS_SCOPE = "https://eventhubs.azure.net/.default";

        private readonly DefaultAzureCredential _defaultAzureCredential;
        private ConcurrentDictionary<string, AccessToken?> _scopeAndTokens = new();


        public TokenStore(DefaultAzureCredential defaultAzureCredential)
        {
            (_defaultAzureCredential) = (defaultAzureCredential);
            RefreshAzureMonitorCredentialOnDemand();
            RefreshAzureEventHubCredentialOnDemand();
        }

        public ValueTask<AccessTokenAndExpiration> RefreshAzureMonitorCredentialOnDemandAsync(CancellationToken cancellationToken = default)
        {
            return RefreshCredentialOnDemand(MONITOR_SCOPE, cancellationToken);
        }

        public ValueTask<AccessTokenAndExpiration> RefreshAzureEventHubCredentialOnDemandAsync(CancellationToken cancellationToken = default)
        {
            return RefreshCredentialOnDemand(EVENTHUBS_SCOPE, cancellationToken);
        }

        public AccessTokenAndExpiration RefreshAzureMonitorCredentialOnDemand(CancellationToken cancellationToken = default)
        {
            return RefreshCredentialOnDemand(MONITOR_SCOPE, cancellationToken).Result;
        }

        public AccessTokenAndExpiration RefreshAzureEventHubCredentialOnDemand(CancellationToken cancellationToken = default)
        {
            return RefreshCredentialOnDemand(EVENTHUBS_SCOPE, cancellationToken).Result;
        }

        public async ValueTask<AccessTokenAndExpiration> RefreshCredentialOnDemand(string scope, CancellationToken cancellationToken = default)
        {
            bool needsNewToken(TimeSpan safetyInterval)
            {
                AccessToken? token;
                if (_scopeAndTokens.TryGetValue(scope, out token))
                {
                    if (!token.HasValue) return true;
                    var timeUntilExpiry = token!.Value.ExpiresOn.Subtract(DateTimeOffset.UtcNow);
                    return timeUntilExpiry < safetyInterval;
                }
                return true;
            }

            var isExpired = needsNewToken(safetyInterval: TimeSpan.FromMinutes(5.0));

            if (isExpired)
            {
                var newToken = await _defaultAzureCredential.GetTokenAsync(
                        requestContext: new TokenRequestContext(new[] { scope }),
                        cancellationToken: cancellationToken);

                AccessToken? token;
                if (_scopeAndTokens.TryGetValue(scope, out token) == false)
                {
                    _scopeAndTokens.TryAdd(scope, newToken);
                }
                else
                {
                    _scopeAndTokens[scope] = newToken;
                }
            }

            return new AccessTokenAndExpiration(isExpired, _scopeAndTokens[scope]!.Value.Token);
        }
    }
}
