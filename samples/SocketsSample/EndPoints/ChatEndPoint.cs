﻿using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Channels;
using Microsoft.AspNetCore.Sockets;

namespace SocketsSample
{
    public class ChatEndPoint : EndPoint
    {
        public ConnectionList Connections { get; } = new ConnectionList();

        public override async Task OnConnected(Connection connection)
        {
            Connections.Add(connection);

            await Broadcast($"{connection.ConnectionId} connected ({connection.Metadata["transport"]})");

            while (true)
            {
                var result = await connection.Channel.Input.ReadAsync();
                var input = result.Buffer;
                try
                {
                    if (input.IsEmpty && result.IsCompleted)
                    {
                        break;
                    }

                    await Broadcast(input);
                }
                finally
                {
                    connection.Channel.Input.Advance(input.End);
                }
            }

            Connections.Remove(connection);

            await Broadcast($"{connection.ConnectionId} disconnected ({connection.Metadata["transport"]})");
        }

        private Task Broadcast(string text)
        {
            return Broadcast(Encoding.UTF8.GetBytes(text));
        }

        private Task Broadcast(byte[] payload)
        {
            var tasks = new List<Task>(Connections.Count);

            foreach (var c in Connections)
            {
                tasks.Add(c.Channel.Output.WriteAsync(payload));
            }

            return Task.WhenAll(tasks);
        }

        private Task Broadcast(ReadableBuffer payload)
        {
            var tasks = new List<Task>(Connections.Count);

            foreach (var c in Connections)
            {
                var write = c.Channel.Output.Alloc();
                write.Append(payload);
                tasks.Add(write.FlushAsync());
            }

            return Task.WhenAll(tasks);
        }
    }

}
