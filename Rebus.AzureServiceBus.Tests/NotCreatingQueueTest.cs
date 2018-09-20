﻿using System;
using Microsoft.ServiceBus;
using NUnit.Framework;
using Rebus.Activation;
using Rebus.AzureServiceBus.Tests.Factories;
using Rebus.Config;
using Rebus.Tests;
using Rebus.Tests.Contracts;

namespace Rebus.AzureServiceBus.Tests
{
    [TestFixture]
    public class NotCreatingQueueTest : FixtureBase
    {
        [Test]
        public void ShouldNotCreateInputQueueWhenConfiguredNotTo()
        {
            var connectionString = AsbTestConfig.ConnectionString;
            var manager = NamespaceManager.CreateFromConnectionString(connectionString);
            var queueName = Guid.NewGuid().ToString("N");

            Assert.IsFalse(manager.QueueExists(queueName));

            var activator = Using(new BuiltinHandlerActivator());

            Configure.With(activator)
                .Logging(l => l.ColoredConsole())
                .Transport(t =>
                {
                    t.UseAzureServiceBus(connectionString, queueName)
                        .DoNotCreateQueues();
                })
                .Start();

            Assert.IsFalse(manager.QueueExists(queueName));
        }
    }
}