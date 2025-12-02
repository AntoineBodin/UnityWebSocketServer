using System.Net.WebSockets;

namespace UnityWebSocketServer
{
    // Manages room state and client collections
    public class RoomManager
    {
        private readonly Dictionary<string, Dictionary<string, ClientInfo>> connectedClientsByRoom = new();
        private readonly Dictionary<string, Dictionary<string, ClientInfo>> pendingClientsByRoom = new();

        public void EnsureRoomExists(string roomId)
        {
            if (!connectedClientsByRoom.ContainsKey(roomId))
                connectedClientsByRoom[roomId] = new();
            if (!pendingClientsByRoom.ContainsKey(roomId))
                pendingClientsByRoom[roomId] = new();
        }

        public void AddPendingClient(string roomId, ClientInfo client)
        {
            pendingClientsByRoom[roomId].Add(client.Id, client);
        }

        public void PromoteToConnected(string roomId, ClientInfo client)
        {
            connectedClientsByRoom[roomId].Add(client.Id, client);
            pendingClientsByRoom[roomId].Remove(client.Id);
        }

        public void RemoveClient(string roomId, string clientId)
        {
            connectedClientsByRoom[roomId].Remove(clientId);
            pendingClientsByRoom[roomId].Remove(clientId);
        }

        public IEnumerable<ClientInfo> GetConnectedClients(string roomId)
        {
            if (!connectedClientsByRoom.TryGetValue(roomId, out var clients))
                return Enumerable.Empty<ClientInfo>();

            return clients.Values.Where(c => c.Socket.State == WebSocketState.Open);
        }

        public ClientInfo? GetClient(string roomId, string clientId)
        {
            if (!connectedClientsByRoom.TryGetValue(roomId, out var clients))
                return null;

            return clients.TryGetValue(clientId, out var client) ? client : null;
        }
    }
}
