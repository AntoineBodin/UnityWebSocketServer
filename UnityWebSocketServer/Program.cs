using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

Dictionary<string, List<WebSocket>> rooms = new();

// Endpoint WebSocket
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    // Récupère la room depuis l'URL : /ws?room=table1
    var query = QueryHelpers.ParseQuery(context.Request.QueryString.Value);
    string roomId = query.ContainsKey("room") ? query["room"].ToString() : "default";

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    if (!rooms.ContainsKey(roomId))
        rooms[roomId] = new List<WebSocket>();

    rooms[roomId].Add(socket);
    Console.WriteLine($"Client connected to room {roomId}");

    var buffer = new byte[4096];
    WebSocketReceiveResult result = null;

    try
    {
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine($"[{roomId}] {message}");

            // Broadcast à tous les clients dans la même room
            foreach (var client in rooms[roomId].ToList())
            {
                if (client.State == WebSocketState.Open)
                {
                    await client.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None
                    );
                }
                else
                {
                    rooms[roomId].Remove(client);
                }
            }

        } while (!result.CloseStatus.HasValue);
    }
    finally
    {
        rooms[roomId].Remove(socket);
        await socket.CloseAsync(result?.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
            result?.CloseStatusDescription ?? "Closed", CancellationToken.None);
        Console.WriteLine($"Client disconnected from room {roomId}");
    }
});

app.Run("http://0.0.0.0:5000");
