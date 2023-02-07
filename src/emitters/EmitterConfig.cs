﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace eventhub_custom_metrics_emitter;

public record EmitterConfig(
    string Region,
    string SubscriptionId,
    string ResourceGroup,
    string TenantId,
    string EventHubNamespace,
    string EventHubName,
    string ConsumerGroup,
    string CheckpointAccountName,
    string CheckpointContainerName,
    int CustomMetricInterval,
    string ManagedIdentityClientId);