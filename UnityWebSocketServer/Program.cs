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
    public record ClientInfo(string Id, WebSocket Socket);

    public class Program
    {
        // Shared rooms dictionary
        private static readonly Dictionary<string, List<ClientInfo>> rooms = new();

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

                // Accept without 'using' so we keep the socket alive until explicit close
                var socket = await context.WebSockets.AcceptWebSocketAsync();

                // Create unique id
                var userId = Guid.NewGuid().ToString("N");

                if (!rooms.ContainsKey(roomId))
                    rooms[roomId] = new List<ClientInfo>();

                var clientInfo = new ClientInfo(userId, socket);
                rooms[roomId].Add(clientInfo);

                Console.WriteLine($"[{roomId}] Client {userId} connected");

                // Send welcome with existing users (including the new one if you want)
                var existingUsers = rooms[roomId]
                    .Where(c => c.Socket.State == WebSocketState.Open)
                    .Select(c => c.Id)
                    .ToList();

                var welcomeMsg = JsonSerializer.Serialize(new
                {
                    type = "welcome",
                    clientId = userId,
                    users = existingUsers
                });

                await socket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(welcomeMsg)),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );

                // Notify others that someone joined
                var joinedMsg = JsonSerializer.Serialize(new
                {
                    type = "user-joined",
                    userId
                });
                await Broadcast(roomId, joinedMsg, except: socket);

                var buffer = new byte[4096];
                WebSocketReceiveResult result = null;

                try
                {
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType != WebSocketMessageType.Text) continue;

                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        Console.WriteLine($"[{roomId}] {userId}: {message}");

                        // If the client sends structured messages (e.g. JSON with a type),
                        // you can parse/inspect them here and act accordingly.
                        // For now we broadcast raw message to the room.
                        await Broadcast(roomId, message);
                    }
                    while (!result.CloseStatus.HasValue);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Socket error for {userId}: {ex}");
                }
                finally
                {
                    // Remove client and try to close the socket cleanly
                    rooms[roomId].Remove(clientInfo);

                    try
                    {
                        if (socket.State != WebSocketState.Closed && socket.State != WebSocketState.Aborted)
                        {
                            await socket.CloseAsync(result?.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                                result?.CloseStatusDescription ?? "Closed",
                                CancellationToken.None);
                        }
                    }
                    catch (Exception) { /* ignore close exceptions */ }

                    Console.WriteLine($"[{roomId}] Client {userId} disconnected");

                    var leftMsg = JsonSerializer.Serialize(new
                    {
                        type = "user-left",
                        userId
                    });
                    await Broadcast(roomId, leftMsg);
                }
            });

            await app.RunAsync("http://0.0.0.0:5000");
        }

        private static async Task Broadcast(string roomId, string message, WebSocket except = null)
        {
            if (!rooms.ContainsKey(roomId)) return;

            foreach (var client in rooms[roomId].ToList())
            {
                try
                {
                    if (client.Socket.State == WebSocketState.Open && client.Socket != except)
                    {
                        await client.Socket.SendAsync(
                            new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None
                        );
                    }
                }
                catch
                {
                    // on erreur, on ignore et on nettoie plus tard
                }
            }
        }
    }
}
