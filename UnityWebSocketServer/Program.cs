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
    public class Player
    {
        public string id { get; set; } = null!;
        public string name { get; set; } = null!;
    }
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var roomManager = new RoomManager();
            var messageHandler = new MessageHandler(roomManager);
            var connectionHandler = new ClientConnectionHandler(roomManager, messageHandler);

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

                // Initialize room
                roomManager.EnsureRoomExists(roomId);

                var socket = await context.WebSockets.AcceptWebSocketAsync();

                // Handle the entire connection lifecycle
                await connectionHandler.HandleConnectionAsync(socket, roomId);
            });

            await app.RunAsync("http://0.0.0.0:5000");
        }
    }
}
