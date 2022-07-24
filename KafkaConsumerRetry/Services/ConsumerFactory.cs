﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using KafkaConsumerRetry.Configuration;

namespace KafkaConsumerRetry.Services {
    /// <summary>
    ///     Starts and subscribes to messages
    /// </summary>
    public class ConsumerFactory : IConsumerFactory {
        private readonly RetryServiceConfig _config;
        private readonly ITopicPartitionQueueAllocator _topicPartitionQueueAllocator;

        public ConsumerFactory(RetryServiceConfig config,
            ITopicPartitionQueueAllocator topicPartitionQueueAllocator) {
            _config = config;
            _topicPartitionQueueAllocator = topicPartitionQueueAllocator;
        }

        public virtual async Task StartConsumers(CancellationToken token, TopicNaming topicNaming) {
            ConsumerBuilder<byte[], byte[]> builder = new(_config.TopicKafka);

            var topicConsumer = builder.Build();
            IConsumer<byte[], byte[]>? retryConsumer = null;

            // setup producer
            if (_config.RetryKafka is { } retryConfig) {
                retryConsumer = new ConsumerBuilder<byte[], byte[]>(retryConfig).Build();
            }

            if (retryConsumer is null) {
                // one consumer for all topics
                var mainAndRetries = new List<string> {topicNaming.Origin};
                mainAndRetries.AddRange(topicNaming.Retries);
                topicConsumer.Subscribe(mainAndRetries);
            }
            else {
                topicConsumer.Subscribe(topicNaming.Origin);
                retryConsumer.Subscribe(topicNaming.Retries);
            }

            // get the group id from the setting for the retry
            string retryGroupId = (_config.RetryKafka ?? _config.TopicKafka)["group.id"];

            List<Task> tasks = new()
                {ConsumeAsync(topicConsumer, topicNaming, token, retryGroupId)};
            if (retryConsumer is { }) {
                tasks.Add(ConsumeAsync(retryConsumer, topicNaming, token, retryGroupId));
            }

            await Task.WhenAll(tasks);
        }

        private async Task ConsumeAsync(IConsumer<byte[], byte[]> consumer, TopicNaming topicNaming,
            CancellationToken cancellationToken, string retryGroupId) {
            await Task.Yield();
            while (!cancellationToken.IsCancellationRequested) {
                var consumeResult = consumer.Consume(cancellationToken);

                // TODO: this is not great. shouldn't return the current index and next topic
                var currentIndexAndNextTopic = GetCurrentIndexAndNextTopic(consumeResult.Topic, topicNaming);
                _topicPartitionQueueAllocator.AddConsumeResult(consumeResult, consumer, retryGroupId, currentIndexAndNextTopic.NextTopic, currentIndexAndNextTopic.CurrentIndex);
            }
        }

        /// <summary>
        /// Gets the current index of the topic, and also the next topic to push to
        /// </summary>
        /// <param name="topic"></param>
        /// <param name="topicNaming"></param>
        /// <returns></returns>
        private (int CurrentIndex, string NextTopic)
            GetCurrentIndexAndNextTopic(string topic, TopicNaming topicNaming) {
            // if straight from the main topic, then use first retry
            if (topic == topicNaming.Origin) {
                return (0, topicNaming.Retries.Any() ? topicNaming.Retries[0] : topicNaming.DeadLetter);
            }

            // if any of the retries except the last, then use the next
            for (var i = 0; i < topicNaming.Retries.Length - 1; i++) {
                if (topicNaming.Retries[i] == topic) {
                    return (i, topicNaming.Retries[i + 1]);
                }
            }

            // otherwise dlq -- must have at least one 
            return (Math.Max(1, topicNaming.Retries.Length), topicNaming.DeadLetter);
        }
    }
}