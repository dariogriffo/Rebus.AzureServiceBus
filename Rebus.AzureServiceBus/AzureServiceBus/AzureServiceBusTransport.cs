﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using Rebus.Bus;
using Rebus.Exceptions;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Subscriptions;
using Rebus.Threading;
using Rebus.Transport;

#pragma warning disable 1998

namespace Rebus.AzureServiceBus
{
    /// <summary>
    /// Implementation of <see cref="ITransport"/> that uses Azure Service Bus queues to send/receive messages.
    /// </summary>
    public class AzureServiceBusTransport : ITransport, IInitializable, IDisposable, ISubscriptionStorage
    {
        const string OutgoingMessagesKey = "azure-service-bus-transport";

        /// <summary>
        /// Subscriber "addresses" are prefixed with this bad boy so we can recognize it and publish to a topic client instead
        /// </summary>
        const string MagicSubscriptionPrefix = "subscription/";

        static readonly TimeSpan[] RetryWaitTimes =
        {
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        };

        readonly ConcurrentDictionary<string, TopicDescription> _topics = new ConcurrentDictionary<string, TopicDescription>(StringComparer.InvariantCultureIgnoreCase);
        readonly ConcurrentDictionary<string, TopicClient> _topicClients = new ConcurrentDictionary<string, TopicClient>(StringComparer.InvariantCultureIgnoreCase);
        readonly ConcurrentDictionary<string, QueueClient> _queueClients = new ConcurrentDictionary<string, QueueClient>(StringComparer.InvariantCultureIgnoreCase);
        readonly string _connectionString;
        readonly IAsyncTaskFactory _asyncTaskFactory;
        readonly string _inputQueueAddress;
        readonly ILog _log;

        readonly TimeSpan _peekLockDuration = TimeSpan.FromMinutes(5);
        readonly AsyncBottleneck _bottleneck = new AsyncBottleneck(10);
        readonly Ignorant _ignorant = new Ignorant();

        readonly ConcurrentQueue<BrokeredMessage> _prefetchQueue = new ConcurrentQueue<BrokeredMessage>();
        readonly TimeSpan? _receiveTimeout;

        bool _prefetchingEnabled;
        int _numberOfMessagesToPrefetch;
        bool _disposed;

        /// <summary>
        /// Constructs the transport, connecting to the service bus pointed to by the connection string.
        /// </summary>
        public AzureServiceBusTransport(string connectionString, string inputQueueAddress, IRebusLoggerFactory rebusLoggerFactory, IAsyncTaskFactory asyncTaskFactory)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));
            if (asyncTaskFactory == null) throw new ArgumentNullException(nameof(asyncTaskFactory));

            _log = rebusLoggerFactory.GetLogger<AzureServiceBusTransport>();

            _connectionString = connectionString;
            _asyncTaskFactory = asyncTaskFactory;

            if (inputQueueAddress != null)
            {
                _inputQueueAddress = inputQueueAddress.ToLowerInvariant();
            }

            // if a timeout has been specified, we respect that - otherwise, we pick a sensible default:
            _receiveTimeout = _connectionString.Contains("OperationTimeout")
                ? default(TimeSpan?)
                : TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// Initializes the transport by ensuring that the input queue has been created
        /// </summary>
        public void Initialize()
        {
            if (_inputQueueAddress != null)
            {
                _log.Info("Initializing Azure Service Bus transport with queue '{0}'", _inputQueueAddress);
                CreateQueue(_inputQueueAddress);
                return;
            }

            _log.Info("Initializing one-way Azure Service Bus transport");
        }

        /// <summary>
        /// Purges the input queue by deleting it and creating it again
        /// </summary>
        public void PurgeInputQueue()
        {
            _log.Info("Purging queue '{0}'", _inputQueueAddress);
            GetNamespaceManager().DeleteQueue(_inputQueueAddress);

            CreateQueue(_inputQueueAddress);
        }

        NamespaceManager GetNamespaceManager() => NamespaceManager.CreateFromConnectionString(_connectionString);

        /// <summary>
        /// Configures the transport to prefetch the specified number of messages into an in-mem queue for processing, disabling automatic peek lock renewal
        /// </summary>
        public void PrefetchMessages(int numberOfMessagesToPrefetch)
        {
            if (numberOfMessagesToPrefetch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(numberOfMessagesToPrefetch), numberOfMessagesToPrefetch, "Must prefetch zero or more messages");
            }

            _prefetchingEnabled = numberOfMessagesToPrefetch > 0;
            _numberOfMessagesToPrefetch = numberOfMessagesToPrefetch;
        }

        /// <summary>
        /// Enables automatic peek lock renewal - only recommended if you truly need to handle messages for a very long time
        /// </summary>
        public bool AutomaticallyRenewPeekLock { get; set; }

        /// <summary>
        /// Creates a queue with the given address
        /// </summary>
        public void CreateQueue(string address)
        {
            if (DoNotCreateQueuesEnabled)
            {
                _log.Info("Transport configured to not create queue - skipping existencecheck and potential creation");
                return;
            }

            if (_inputQueueAddress == null)
            {
                _log.Info("One-way client shoult not create any queues - skipping existencecheck and potential creation");
                return;
            }

            if (GetNamespaceManager().QueueExists(address)) return;

            var now = DateTime.Now;
            var queueDescription = new QueueDescription(address)
            {
                MaxSizeInMegabytes = 1024,
                MaxDeliveryCount = 100,
                LockDuration = _peekLockDuration,
                EnablePartitioning = PartitioningEnabled,
                UserMetadata = $"Created by Rebus {now:yyyy-MM-dd} - {now:HH:mm:ss}",
            };

            try
            {
                _log.Info("Queue '{0}' does not exist - will create it now", address);
                GetNamespaceManager().CreateQueue(queueDescription);
                _log.Info("Created!");
            }
            catch (MessagingEntityAlreadyExistsException)
            {
                // fair enough...
                _log.Info("MessagingEntityAlreadyExistsException - carrying on");
            }
        }

        /// <summary>
        /// Gets/sets whether partitioning should be enabled on new queues. Only takes effect for queues created
        /// after the property has been enabled
        /// </summary>
        public bool PartitioningEnabled { get; set; }

        /// <summary>
        /// Sends the given message to the queue with the given <paramref name="destinationAddress"/>
        /// </summary>
        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            GetOutgoingMessages(context)
                .GetOrAdd(destinationAddress, _ => new ConcurrentQueue<TransportMessage>())
                .Enqueue(message);
        }


        /// <summary>
        /// Should return a new <see cref="Retrier"/>, fully configured to correctly "accept" the right exceptions
        /// </summary>
        static Retrier GetRetrier()
        {
            return new Retrier(RetryWaitTimes)
                .On<MessagingException>(e => e.IsTransient)
                .On<MessagingCommunicationException>(e => e.IsTransient)
                .On<ServerBusyException>(e => e.IsTransient);
        }

        /// <summary>
        /// Receives the next message from the input queue. Returns null if no message was available
        /// </summary>
        public async Task<TransportMessage> Receive(ITransactionContext context, CancellationToken cancellationToken)
        {
            if (_inputQueueAddress == null)
            {
                throw new InvalidOperationException("This Azure Service Bus transport does not have an input queue, hence it is not possible to reveive anything");
            }

            using (await _bottleneck.Enter(cancellationToken).ConfigureAwait(false))
            {
                var brokeredMessage = await ReceiveBrokeredMessage().ConfigureAwait(false);

                if (brokeredMessage == null) return null;

                var headers = brokeredMessage.Properties
                    .Where(kvp => kvp.Value is string)
                    .ToDictionary(kvp => kvp.Key, kvp => (string)kvp.Value);

                var messageId = headers.GetValueOrNull(Headers.MessageId);
                var now = DateTime.UtcNow;
                var leaseDuration = brokeredMessage.LockedUntilUtc - now;
                var lockRenewalInterval = TimeSpan.FromMinutes(0.5 * leaseDuration.TotalMinutes);

                var renewalTask = GetRenewalTaskOrFakeDisposable(messageId, brokeredMessage, lockRenewalInterval);

                context.OnAborted(() =>
                {
                    renewalTask.Dispose();

                    try
                    {
                        brokeredMessage.Abandon();
                    }
                    catch (Exception exception)
                    {
                        // if it fails, it'll be back on the queue anyway....
                        _log.Warn("Could not abandon message: {0}", exception);
                    }
                });

                context.OnCommitted(async () =>
                {
                    renewalTask.Dispose();
                });

                context.OnCompleted(async () =>
                {
                    try
                    {
                        await brokeredMessage.CompleteAsync().ConfigureAwait(false);
                    }
                    catch (MessageLockLostException exception)
                    {
                        var elapsed = DateTime.UtcNow - now;

                        throw new RebusApplicationException(exception, $"The message lock for message with ID {messageId} was lost - tried to complete after {elapsed.TotalSeconds:0.0} s");
                    }
                });

                context.OnDisposed(() =>
                {
                    renewalTask.Dispose();

                    brokeredMessage.Dispose();
                });

                using (var memoryStream = new MemoryStream())
                {
                    await brokeredMessage.GetBody<Stream>().CopyToAsync(memoryStream).ConfigureAwait(false);
                    return new TransportMessage(headers, memoryStream.ToArray());
                }
            }
        }

        ConcurrentDictionary<string, ConcurrentQueue<TransportMessage>> GetOutgoingMessages(ITransactionContext context)
        {
            return context.GetOrAdd(OutgoingMessagesKey, () =>
            {
                var destinations = new ConcurrentDictionary<string, ConcurrentQueue<TransportMessage>>();

                context.OnCommitted(async () =>
                {
                    // send outgoing messages
                    foreach (var destinationAndMessages in destinations)
                    {
                        var destinationAddress = destinationAndMessages.Key;
                        var messages = destinationAndMessages.Value;

                        var sendTasks = messages
                            .Select(async message =>
                            {
                                await GetRetrier().Execute(async () =>
                                {
                                    using (var brokeredMessageToSend = MsgHelpers.CreateBrokeredMessage(message))
                                    {
                                        try
                                        {
                                            await Send(destinationAddress, brokeredMessageToSend).ConfigureAwait(false);
                                        }
                                        catch (MessagingEntityNotFoundException exception)
                                        {
                                            // do NOT rethrow as MessagingEntityNotFoundException because it has its own ToString that swallows most of the info!!
                                            throw new MessagingException($"Could not send to '{destinationAddress}'!", false, exception);
                                        }
                                    }
                                }).ConfigureAwait(false);
                            })
                            .ToArray();

                        await Task.WhenAll(sendTasks).ConfigureAwait(false);
                    }
                });

                return destinations;
            });
        }

        async Task Send(string destinationAddress, BrokeredMessage brokeredMessageToSend)
        {
            if (destinationAddress.StartsWith(MagicSubscriptionPrefix))
            {
                var topic = destinationAddress.Substring(MagicSubscriptionPrefix.Length);

                await GetTopicClient(topic).SendAsync(brokeredMessageToSend).ConfigureAwait(false);
            }
            else
            {
                await GetQueueClient(destinationAddress).SendAsync(brokeredMessageToSend).ConfigureAwait(false);
            }
        }

        TopicClient GetTopicClient(string topic)
        {
            return _topicClients.GetOrAdd(topic, t =>
            {
                _log.Debug("Initializing new topic client for {0}", topic);

                var topicDescription = EnsureTopicExists(topic);

                var fromConnectionString = TopicClient.CreateFromConnectionString(_connectionString, topicDescription.Path);

                return fromConnectionString;
            });
        }


        IDisposable GetRenewalTaskOrFakeDisposable(string messageId, BrokeredMessage brokeredMessage, TimeSpan lockRenewalInterval)
        {
            if (!AutomaticallyRenewPeekLock)
            {
                return new FakeDisposable();
            }

            if (_prefetchingEnabled)
            {
                return new FakeDisposable();
            }

            var renewalTask = _asyncTaskFactory
                .Create($"RenewPeekLock-{messageId}",
                    async () =>
                    {
                        await RenewPeekLock(messageId, brokeredMessage).ConfigureAwait(false);
                    },
                    intervalSeconds: (int)lockRenewalInterval.TotalSeconds,
                    prettyInsignificant: true);

            renewalTask.Start();

            return renewalTask;
        }

        async Task RenewPeekLock(string messageId, BrokeredMessage brokeredMessage)
        {
            _log.Info("Renewing peek lock for message with ID {0}", messageId);

            try
            {
                await brokeredMessage.RenewLockAsync().ConfigureAwait(false);
            }
            catch (MessageLockLostException)
            {
                // if we get this, it is because the message has been handled
            }
        }

        class FakeDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        async Task<BrokeredMessage> ReceiveBrokeredMessage()
        {
            var queueAddress = _inputQueueAddress;

            if (_prefetchingEnabled)
            {
                BrokeredMessage nextMessage;

                if (_prefetchQueue.TryDequeue(out nextMessage))
                {
                    return nextMessage;
                }

                var client = GetQueueClient(queueAddress);

                // Timeout can be specified in ASB ConnectionString Endpoint=sb:://...;OperationTimeout=00:00:10
                var brokeredMessages = _receiveTimeout.HasValue
                    ? (await client.ReceiveBatchAsync(_numberOfMessagesToPrefetch, _receiveTimeout.Value).ConfigureAwait(false)).ToList()
                    : (await client.ReceiveBatchAsync(_numberOfMessagesToPrefetch).ConfigureAwait(false)).ToList();

                _ignorant.Reset();

                if (!brokeredMessages.Any()) return null;

                foreach (var receivedMessage in brokeredMessages)
                {
                    _prefetchQueue.Enqueue(receivedMessage);
                }

                _prefetchQueue.TryDequeue(out nextMessage);

                return nextMessage; //< just accept null at this point if there was nothing
            }

            try
            {
                // Timeout can be specified in ASB ConnectionString Endpoint=sb:://...;OperationTimeout=00:00:10
                var brokeredMessage = _receiveTimeout.HasValue
                    ? await GetQueueClient(queueAddress).ReceiveAsync(_receiveTimeout.Value).ConfigureAwait(false)
                    : await GetQueueClient(queueAddress).ReceiveAsync().ConfigureAwait(false);

                _ignorant.Reset();

                return brokeredMessage;
            }
            catch (Exception exception)
            {
                if (_ignorant.IsToBeIgnored(exception)) return null;

                QueueClient possiblyFaultyQueueClient;

                if (_queueClients.TryRemove(queueAddress, out possiblyFaultyQueueClient))
                {
                    CloseQueueClient(possiblyFaultyQueueClient);
                }

                throw;
            }
        }

        static void CloseQueueClient(QueueClient queueClientToClose)
        {
            try
            {
                queueClientToClose.Close();
            }
            catch (Exception)
            {
                // ignored because we don't care!
            }
        }

        QueueClient GetQueueClient(string queueAddress)
        {
            var queueClient = _queueClients.GetOrAdd(queueAddress, address =>
            {
                _log.Debug("Initializing new queue client for {0}", address);

                var connectionStringParser = new ConnectionStringParser(_connectionString);
                var connectionStringWithoutEntityPath = connectionStringParser.GetConnectionStringWithoutEntityPath();

                var newQueueClient = QueueClient.CreateFromConnectionString(connectionStringWithoutEntityPath, address, ReceiveMode.PeekLock);

                return newQueueClient;
            });

            return queueClient;
        }

        /// <summary>
        /// Gets the address of the input queue for the transport
        /// </summary>
        public string Address => _inputQueueAddress;

        /// <summary>
        /// Releases prefetched messages and cached queue clients
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                DisposePrefetchedMessages();

                _queueClients.Values.ForEach(CloseQueueClient);
            }
            finally
            {
                _disposed = true;
            }
        }

        void DisposePrefetchedMessages()
        {
            BrokeredMessage brokeredMessage;
            while (_prefetchQueue.TryDequeue(out brokeredMessage))
            {
                using (brokeredMessage)
                {
                    try
                    {
                        brokeredMessage.Abandon();
                    }
                    catch (Exception exception)
                    {
                        _log.Warn("Could not abandon brokered message with ID {0}: {1}", brokeredMessage.MessageId, exception);
                    }
                }
            }
        }

        /// <summary>
        /// Gets "subscriber addresses" by getting one single magic "queue name", which is then
        /// interpreted as a publish operation to a topic when the time comes to send to that "queue"
        /// </summary>
        public async Task<string[]> GetSubscriberAddresses(string topic)
        {
            var normalizedTopic = topic.ToValidAzureServiceBusEntityName();

            return new[] { $"{MagicSubscriptionPrefix}{normalizedTopic}" };
        }

        /// <summary>
        /// Registers this endpoint as a subscriber by creating a subscription for the given topic, setting up
        /// auto-forwarding from that subscription to this endpoint's input queue
        /// </summary>
        public async Task RegisterSubscriber(string topic, string subscriberAddress)
        {
            VerifyIsOwnInputQueueAddress(subscriberAddress);

            var normalizedTopic = topic.ToValidAzureServiceBusEntityName();
            var topicDescription = EnsureTopicExists(normalizedTopic);
            var inputQueueClient = GetQueueClient(_inputQueueAddress);

            var inputQueuePath = inputQueueClient.Path;
            var topicPath = topicDescription.Path;
            var subscriptionName = GetSubscriptionName();

            var subscription = await GetOrCreateSubscription(topicPath, subscriptionName).ConfigureAwait(false);
            subscription.ForwardTo = inputQueuePath;
            await GetNamespaceManager().UpdateSubscriptionAsync(subscription).ConfigureAwait(false);
        }

        async Task<SubscriptionDescription> GetOrCreateSubscription(string topicPath, string subscriptionName)
        {
            try
            {
                return await GetNamespaceManager().CreateSubscriptionAsync(topicPath, subscriptionName).ConfigureAwait(false);
            }
            catch (MessagingEntityAlreadyExistsException)
            {
                return await GetNamespaceManager().GetSubscriptionAsync(topicPath, subscriptionName).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Unregisters this endpoint as a subscriber by deleting the subscription for the given topic
        /// </summary>
        public async Task UnregisterSubscriber(string topic, string subscriberAddress)
        {
            VerifyIsOwnInputQueueAddress(subscriberAddress);

            var normalizedTopic = topic.ToValidAzureServiceBusEntityName();
            var topicDescription = EnsureTopicExists(normalizedTopic);
            var topicPath = topicDescription.Path;
            var subscriptionName = GetSubscriptionName();

            try
            {
                await GetNamespaceManager().DeleteSubscriptionAsync(topicPath, subscriptionName).ConfigureAwait(false);
            }
            catch (MessagingEntityNotFoundException) { }
        }

        string GetSubscriptionName()
        {
            var idx = _inputQueueAddress.LastIndexOf("/", StringComparison.Ordinal) + 1;
            return _inputQueueAddress.Substring(idx).ToValidAzureServiceBusEntityName();
        }

        void VerifyIsOwnInputQueueAddress(string subscriberAddress)
        {
            if (subscriberAddress == _inputQueueAddress) return;

            var message = $"Cannot register subscriptions endpoint with input queue '{subscriberAddress}' in endpoint with input" +
                          $" queue '{_inputQueueAddress}'! The Azure Service Bus transport functions as a centralized subscription" +
                          " storage, which means that all subscribers are capable of managing their own subscriptions";

            throw new ArgumentException(message);
        }

        TopicDescription EnsureTopicExists(string normalizedTopic)
        {
            return _topics.GetOrAdd(normalizedTopic, t =>
            {
                try
                {
                    return GetNamespaceManager().CreateTopic(normalizedTopic);
                }
                catch (MessagingEntityAlreadyExistsException)
                {
                    return GetNamespaceManager().GetTopic(normalizedTopic);
                }
                catch (Exception exception)
                {
                    throw new ArgumentException($"Could not create topic '{normalizedTopic}'", exception);
                }
            });
        }

        /// <summary>
        /// Always returns true because Azure Service Bus topics and subscriptions are global
        /// </summary>
        public bool IsCentralized => true;

        /// <summary>
        /// Gets/sets whether to skip creating queues
        /// </summary>
        public bool DoNotCreateQueuesEnabled { get; set; }
    }
}
