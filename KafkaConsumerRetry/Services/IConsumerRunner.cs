﻿using KafkaConsumerRetry.Configuration;

namespace KafkaConsumerRetry.Services;

public interface IConsumerRunner {
    Task RunConsumersAsync<TResultHandler>(KafkaRetryConfig kafkaRetryConfig, TopicNames topicNames,
        CancellationToken token) where TResultHandler : IConsumerResultHandler;
}