using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace UnityWebSocketServer
{
    // Handles WebSocket message sending and serialization
    public class MessageHandler
    {
        private readonly RoomManager _roomManager;

        public MessageHandler(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        public async Task SendAsync(WebSocket socket, object message)
        {
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }

        public async Task SendWelcomeMessageAsync(string roomId, WebSocket socket, string userId)
        {
            var existingUsers = _roomManager.GetConnectedClients(roomId)
                .Select(c => new Player { id = c.Id, name = c.Name })
                .ToArray();

            var welcomeMsg = new
            {
                type = "welcome",
                clientId = userId,
                users = existingUsers
            };

            await SendAsync(socket, welcomeMsg);
        }

        public async Task BroadcastMessageAsync(string roomId, object message, WebSocket? except = null)
        {
            var json = JsonSerializer.Serialize(message);
            await BroadcastRawAsync(roomId, json, except);
        }

        private async Task BroadcastRawAsync(string roomId, string message, WebSocket? except = null)
        {
            var bytes = Encoding.UTF8.GetBytes(message);

            foreach (var client in _roomManager.GetConnectedClients(roomId))
            {
                if (client.Socket == except)
                    continue;

                try
                {
                    await client.Socket.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
                catch { /* Ignore send failures */ }
            }
        }
    }

    [Serializable]
    public class BaseMessage
    {
        public string type { get; set; } = null!;
        public string clientId { get; set; } = null!;
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
    public class PlayerLeftMessage : BaseMessage
    {
        public const string MessageTypeConst = "player_left";

        public PlayerLeftMessage()
        {
            type = MessageTypeConst;
        }
    }
}
