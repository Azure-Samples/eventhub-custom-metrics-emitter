namespace custom_metrics_emitter.emitters;

using System.Text.Json.Serialization;

// https://learn.microsoft.com/en-us/azure/azure-monitor/essentials/metrics-store-custom-rest-api

#pragma warning disable IDE1006 // Naming Styles

public record EmitterSchema(
    DateTime time,
    CustomMetricData? data);

public record CustomMetricData(
    CustomMetricBaseData? baseData);

public record CustomMetricBaseData(
    string? metric,
    string? Namespace,
    IEnumerable<string>? dimNames,
    IEnumerable<CustomMetricBaseDataSeriesItem>? series);

public record CustomMetricBaseDataSeriesItem(
    IEnumerable<string>? dimValues,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? min,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? max,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? sum,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? count);

#pragma warning restore IDE1006 // Naming Styles
