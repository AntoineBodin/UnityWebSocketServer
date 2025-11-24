using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;

namespace UnityWebSocketServer
{
    public record ClientInfo(string Id, string Name, WebSocket Socket);

    [Serializable]
    public class BaseMessage
    {
        public string type { get; set; } = null!;
        public string clientId { get; set; } = null!;
    }

    [Serializable]
    public class MyNameIsMessage : BaseMessage
    {
        public const string MessageTypeConst = "my_name_is";
        public string name { get; set; } = null!;

        public MyNameIsMessage()
        {
            type = MessageTypeConst;
        }
    }

    [Serializable]
    public class WhoIsMessage : BaseMessage
    {
        public const string MessageTypeConst = "who_is";
        public string queryClientId { get; set; } = null!;

        public WhoIsMessage()
        {
            type = MessageTypeConst;
        }
    }

    [Serializable]
    public class PlayerJoinedMessage : BaseMessage
    {
        public const string MessageTypeConst = "player_joined";
        public Player player { get; set; } = null!;

        public PlayerJoinedMessage()
        {
            type = MessageTypeConst;
        }
    }

    [Serializable]
    public class Player
    {
        public string id { get; set; } = null!;
        public string name { get; set; } = null!;
    }

    [Serializable]
    public class WelcomeMessage : BaseMessage
    {
        public const string MessageTypeConst = "welcome";
        public Player[] users { get; set; } = Array.Empty<Player>();

        public WelcomeMessage()
        {
            type = MessageTypeConst;
        }
    }

    public class Program
    {
        private static readonly Dictionary<string, Dictionary<string, ClientInfo>> connectedClientsByRoom = new();
        private static readonly Dictionary<string, Dictionary<string, ClientInfo>> pendingClientsByRoom = new();

        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            app.UseWebSockets();

            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var query = QueryHelpers.ParseQuery(context.Request.QueryString.Value);
                string roomId = query.ContainsKey("room") ? query["room"].ToString() : "default";

                // Initialize room lists if necessary
                if (!connectedClientsByRoom.ContainsKey(roomId))
                    connectedClientsByRoom[roomId] = new();
                if (!pendingClientsByRoom.ContainsKey(roomId))
                    pendingClientsByRoom[roomId] = new();

                var socket = await context.WebSockets.AcceptWebSocketAsync();
                var userId = Guid.NewGuid().ToString("N");

                // Add temp client to pending
                var clientInfo = new ClientInfo(userId, "temp", socket);
                pendingClientsByRoom[roomId].Add(userId, clientInfo);

                // Send welcome with existing users
                await SendWelcomeMessage(roomId, socket, userId);
                Console.WriteLine($"[{roomId}] pending client {userId} connecting");

                // Wait for the client to send its name
                var finalInfo = await WaitForPlayerInfoAsync(socket, TimeSpan.FromSeconds(5), roomId, clientInfo);
                if (finalInfo == null) return; // timeout or handshake failed

                // Main loop for regular messages
                var buffer = new byte[4096];
                WebSocketReceiveResult result = null;

                try
                {
                    while (!socket.CloseStatus.HasValue)
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType != WebSocketMessageType.Text) continue;

                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        Console.WriteLine($"[{roomId}] {finalInfo.Id}: {message}");

                        BaseMessage? baseMessage = JsonSerializer.Deserialize<BaseMessage>(message);
                        
                        if (baseMessage == null)
                            continue;
                        else if (baseMessage != null && baseMessage.type == WhoIsMessage.MessageTypeConst)
                        {
                            var whoIsMsg = JsonSerializer.Deserialize<WhoIsMessage>(message);
                            if (whoIsMsg != null)
                            {
                                var queriedClient = connectedClientsByRoom[roomId][whoIsMsg.queryClientId];
                                var responseMsg = JsonSerializer.Serialize(
                                    new PlayerJoinedMessage()
                                    {
                                        clientId = whoIsMsg.queryClientId,
                                        player = new Player() 
                                        { 
                                            id = whoIsMsg.queryClientId, 
                                            name = queriedClient?.Name ?? "unknown" 
                                        }
                                    });
                                // Send response only to the requester
                                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(responseMsg)),
                                    WebSocketMessageType.Text,
                                    true,
                                    CancellationToken.None);
                                continue;
                            }
                        }


                        // Broadcast to other clients in the room
                        await Broadcast(roomId, message, except: socket);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Socket error for {finalInfo.Id}: {ex}");
                }
                finally
                {
                    // Cleanup on disconnect
                    connectedClientsByRoom[roomId].Remove(finalInfo.Id);
                    pendingClientsByRoom[roomId].Remove(finalInfo.Id);

                    try
                    {
                        if (socket.State != WebSocketState.Closed && socket.State != WebSocketState.Aborted)
                        {
                            await socket.CloseAsync(result?.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                                result?.CloseStatusDescription ?? "Closed",
                                CancellationToken.None);
                        }
                    }
                    catch { /* ignore */ }

                    Console.WriteLine($"[{roomId}] Client {finalInfo.Id} disconnected");

                    var leftMsg = JsonSerializer.Serialize(new
                    {
                        type = "user-left",
                        userId = finalInfo.Id
                    });
                    await Broadcast(roomId, leftMsg);
                }
            });

            await app.RunAsync("http://0.0.0.0:5000");
        }

        private static async Task SendWelcomeMessage(string roomId, WebSocket socket, string userId)
        {
            var existingUsers = connectedClientsByRoom[roomId]
                .Where(c => c.Value.Socket.State == WebSocketState.Open)
                .Select(c => new Player { id = c.Value.Id, name = c.Value.Name })
                .ToList();

            string welcomeMsg = JsonSerializer.Serialize(new
            {
                type = "welcome",
                clientId = userId,
                users = existingUsers
            });

            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(welcomeMsg)),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }

        private static async Task<ClientInfo?> WaitForPlayerInfoAsync(
            WebSocket socket, TimeSpan timeout, string roomId, ClientInfo clientInfo)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buffer = new byte[4096];

            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var myNameMsg = JsonSerializer.Deserialize<MyNameIsMessage>(message);

                    if (myNameMsg == null || myNameMsg.type != "my_name_is") continue;

                    // Update clientInfo with real name
                    clientInfo = new ClientInfo(clientInfo.Id, myNameMsg.name, socket);

                    connectedClientsByRoom[roomId].Add(clientInfo.Id, clientInfo);
                    pendingClientsByRoom[roomId].Remove(clientInfo.Id);

                    // Broadcast user-joined to other clients
                    var joinedMsg = JsonSerializer.Serialize(new PlayerJoinedMessage()
                    {
                        clientId = clientInfo.Id,
                        player = new Player() { id = clientInfo.Id, name = clientInfo.Name }
                    });
                    await Broadcast(roomId, joinedMsg, except: socket);

                    Console.WriteLine($"[{roomId}] {clientInfo.Id} joined as '{clientInfo.Name}'");
                    return clientInfo;
                }
            }
            catch (OperationCanceledException)
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                    "Timeout waiting for player-info", CancellationToken.None);
                return null;
            }
        }

        private static async Task Broadcast(string roomId, string message, WebSocket? except = null)
        {
            if (!connectedClientsByRoom.TryGetValue(roomId, out Dictionary<string, ClientInfo>? value)) return;

            foreach (var client in value.ToList())
            {
                try
                {
                    if (client.Value.Socket.State == WebSocketState.Open && client.Value.Socket != except)
                    {
                        await client.Value.Socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                    }
                }
                catch { /* ignore */ }
            }
        }
    }
}
