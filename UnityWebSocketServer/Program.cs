using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Pas besoin de services MVC si tu ne fais pas d'API
// builder.Services.AddControllers();

var app = builder.Build();

// Active WebSockets
app.UseWebSockets();

// WebSocket endpoint
List<WebSocket> clients = new();

app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        clients.Add(socket);
        Console.WriteLine("Client connected");

        var buffer = new byte[1024 * 4];
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine($"Received: {msg}");

            // Broadcast to all clients
            foreach (var client in clients.ToList())
            {
                if (client.State == WebSocketState.Open)
                {
                    var response = Encoding.UTF8.GetBytes(msg);
                    await client.SendAsync(new ArraySegment<byte>(response), result.MessageType, true, CancellationToken.None);
                }
            }

        } while (!result.CloseStatus.HasValue);

        clients.Remove(socket);
        await socket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        Console.WriteLine("Client disconnected");
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

// Run on all network interfaces, port 5000
app.Run("http://0.0.0.0:5000");
