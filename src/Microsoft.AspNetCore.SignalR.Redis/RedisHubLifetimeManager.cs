﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Channels;
using Microsoft.AspNetCore.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Microsoft.AspNetCore.SignalR.Redis
{
    public class RedisHubLifetimeManager<THub> : HubLifetimeManager<THub>, IDisposable
    {
        // TODO: Use something more efficient for sending data over the bus
        private readonly JsonSerializer _serializer = new JsonSerializer();
        private readonly ConnectionList _connections = new ConnectionList();

        // TODO: Investigate "memory leak" entries never get removed
        private readonly ConcurrentDictionary<string, GroupData> _groups = new ConcurrentDictionary<string, GroupData>();
        private readonly InvocationAdapterRegistry _registry;
        private readonly ConnectionMultiplexer _redisServerConnection;
        private readonly ISubscriber _bus;
        private readonly ILoggerFactory _loggerFactory;
        private readonly RedisOptions _options;

        public RedisHubLifetimeManager(InvocationAdapterRegistry registry,
                                       ILoggerFactory loggerFactory,
                                       IOptions<RedisOptions> options)
        {
            _loggerFactory = loggerFactory;
            _registry = registry;
            _options = options.Value;

            var writer = new LoggerTextWriter(loggerFactory.CreateLogger<RedisHubLifetimeManager<THub>>());
            _redisServerConnection = _options.Connect(writer);
            _bus = _redisServerConnection.GetSubscriber();

            var previousBroadcastTask = Task.CompletedTask;

            _bus.Subscribe(typeof(THub).FullName, async (c, data) =>
            {
                await previousBroadcastTask;

                var tasks = new List<Task>(_connections.Count);

                foreach (var connection in _connections)
                {
                    tasks.Add(connection.Channel.Output.WriteAsync((byte[])data));
                }

                previousBroadcastTask = Task.WhenAll(tasks);
            });
        }

        public override Task InvokeAllAsync(string methodName, object[] args)
        {
            var message = new InvocationDescriptor
            {
                Method = methodName,
                Arguments = args
            };

            return PublishAsync(typeof(THub).FullName, message);
        }

        public override Task InvokeConnectionAsync(string connectionId, string methodName, object[] args)
        {
            var message = new InvocationDescriptor
            {
                Method = methodName,
                Arguments = args
            };

            return PublishAsync(typeof(THub).FullName + "." + connectionId, message);
        }

        public override Task InvokeGroupAsync(string groupName, string methodName, object[] args)
        {
            var message = new InvocationDescriptor
            {
                Method = methodName,
                Arguments = args
            };

            return PublishAsync(typeof(THub).FullName + "." + groupName, message);
        }

        public override Task InvokeUserAsync(string userId, string methodName, object[] args)
        {
            var message = new InvocationDescriptor
            {
                Method = methodName,
                Arguments = args
            };

            return PublishAsync(typeof(THub).FullName + "." + userId, message);
        }

        private async Task PublishAsync(string channel, InvocationDescriptor message)
        {
            byte[] data = ToBytes(message);
            await _bus.PublishAsync(channel, data);
        }

        public override Task OnConnectedAsync(Connection connection)
        {
            var redisSubscriptions = connection.Metadata.GetOrAdd("redis_subscriptions", _ => new HashSet<string>());
            var connectionTask = Task.CompletedTask;
            var userTask = Task.CompletedTask;

            _connections.Add(connection);

            var connectionChannel = typeof(THub).FullName + "." + connection.ConnectionId;
            redisSubscriptions.Add(connectionChannel);

            var previousConnectionTask = Task.CompletedTask;

            connectionTask = _bus.SubscribeAsync(connectionChannel, async (c, data) =>
            {
                await previousConnectionTask;

                previousConnectionTask = connection.Channel.Output.WriteAsync((byte[])data);
            });


            if (connection.User.Identity.IsAuthenticated)
            {
                var userChannel = typeof(THub).FullName + "." + connection.User.Identity.Name;
                redisSubscriptions.Add(userChannel);

                var previousUserTask = Task.CompletedTask;

                // TODO: Look at optimizing (looping over connections checking for Name)
                userTask = _bus.SubscribeAsync(userChannel, async (c, data) =>
                {
                    await previousUserTask;

                    previousUserTask = connection.Channel.Output.WriteAsync((byte[])data);
                });
            }

            return Task.WhenAll(connectionTask, userTask);
        }

        public override Task OnDisconnectedAsync(Connection connection)
        {
            _connections.Remove(connection);

            var tasks = new List<Task>();

            var redisSubscriptions = connection.Metadata.Get<HashSet<string>>("redis_subscriptions");
            if (redisSubscriptions != null)
            {
                foreach (var subscription in redisSubscriptions)
                {
                    tasks.Add(_bus.UnsubscribeAsync(subscription));
                }
            }

            var groupNames = connection.Metadata.Get<HashSet<string>>("group");

            if (groupNames != null)
            {
                foreach (var group in groupNames)
                {
                    tasks.Add(RemoveGroupAsync(connection, group));
                }
            }

            return Task.WhenAll(tasks);
        }

        public override async Task AddGroupAsync(Connection connection, string groupName)
        {
            var groupChannel = typeof(THub).FullName + "." + groupName;

            var groupNames = connection.Metadata.GetOrAdd("group", _ => new HashSet<string>());
            groupNames.Add(groupName);

            var group = _groups.GetOrAdd(groupChannel, _ => new GroupData());

            await group.Lock.WaitAsync();
            try
            {
                group.Connections.Add(connection);

                // Subscribe once
                if (group.Connections.Count > 1)
                {
                    return;
                }

                var previousTask = Task.CompletedTask;

                await _bus.SubscribeAsync(groupChannel, async (c, data) =>
                {
                    // Since this callback is async, we await the previous task then
                    // before sending the current message. This is because we don't
                    // want to do concurrent writes to the outgoing connections
                    await previousTask;

                    var tasks = new List<Task>(group.Connections.Count);
                    foreach (var groupConnection in group.Connections)
                    {
                        tasks.Add(groupConnection.Channel.Output.WriteAsync((byte[])data));
                    }

                    previousTask = Task.WhenAll(tasks);
                });
            }
            finally
            {
                group.Lock.Release();
            }
        }

        public override async Task RemoveGroupAsync(Connection connection, string groupName)
        {
            var groupChannel = typeof(THub).FullName + "." + groupName;

            GroupData group;
            if (!_groups.TryGetValue(groupChannel, out group))
            {
                return;
            }

            var groupNames = connection.Metadata.Get<HashSet<string>>("group");
            groupNames?.Remove(groupName);

            await group.Lock.WaitAsync();
            try
            {
                group.Connections.Remove(connection);

                if (group.Connections.Count == 0)
                {
                    await _bus.UnsubscribeAsync(groupChannel);
                }
            }
            finally
            {
                group.Lock.Release();
            }
        }

        public void Dispose()
        {
            _bus.UnsubscribeAll();
            _redisServerConnection.Dispose();
        }

        private byte[] ToBytes(InvocationDescriptor message)
        {
            // TODO: Pool these
            var ms = new MemoryStream();
            var jsonWriter = new JsonTextWriter(new StreamWriter(ms));

            _serializer.Serialize(jsonWriter, message);
            jsonWriter.Flush();
            return ms.ToArray();
        }

        private InvocationDescriptor FromBytes(byte[] data)
        {
            var ms = new MemoryStream(data);
            var jsonReader = new JsonTextReader(new StreamReader(ms));

            return _serializer.Deserialize<InvocationDescriptor>(jsonReader);
        }

        private class LoggerTextWriter : TextWriter
        {
            private readonly ILogger _logger;

            public LoggerTextWriter(ILogger logger)
            {
                _logger = logger;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {

            }

            public override void WriteLine(string value)
            {
                _logger.LogDebug(value);
            }
        }

        private class GroupData
        {
            public SemaphoreSlim Lock = new SemaphoreSlim(1, 1);
            public ConnectionList Connections = new ConnectionList();
        }
    }
}
