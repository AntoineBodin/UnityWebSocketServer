using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace UnityWebSocketServer
{
    // Handles client connection lifecycle
    public class ClientConnectionHandler
    {
        private readonly RoomManager _roomManager;
        private readonly MessageHandler _messageHandler;
        private const int BufferSize = 4096;
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan TimeoutThreshold = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

        public ClientConnectionHandler(RoomManager roomManager, MessageHandler messageHandler)
        {
            _roomManager = roomManager;
            _messageHandler = messageHandler;
        }

        public async Task HandleConnectionAsync(WebSocket socket, string roomId)
        {
            var userId = Guid.NewGuid().ToString("N");
            var clientInfo = new ClientInfo(userId, "temp", socket);

            // Add to pending clients
            _roomManager.AddPendingClient(roomId, clientInfo);

            // Send welcome message
            await _messageHandler.SendWelcomeMessageAsync(roomId, socket, userId);
            Console.WriteLine($"[{roomId}] pending client {userId} connecting");

            // Wait for handshake
            var finalInfo = await WaitForHandshakeAsync(socket, roomId, clientInfo);
            if (finalInfo == null) return; // Handshake failed

            // Handle message loop
            await HandleMessageLoopAsync(socket, roomId, finalInfo);

            // Cleanup
            await CleanupClientAsync(socket, roomId, finalInfo);
        }

        private async Task<ClientInfo?> WaitForHandshakeAsync(WebSocket socket, string roomId, ClientInfo clientInfo)
        {
            using var cts = new CancellationTokenSource(HandshakeTimeout);
            var buffer = new byte[BufferSize];

            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var myNameMsg = JsonSerializer.Deserialize<MyNameIsMessage>(message);

                    if (myNameMsg == null || myNameMsg.type != MyNameIsMessage.MessageTypeConst) continue;

                    // Update client with real name
                    clientInfo = new ClientInfo(clientInfo.Id, myNameMsg.name, socket);
                    _roomManager.PromoteToConnected(roomId, clientInfo);

                    // Notify other clients
                    var joinedMsg = new PlayerJoinedMessage()
                    {
                        clientId = clientInfo.Id,
                        player = new Player() { id = clientInfo.Id, name = clientInfo.Name }
                    };
                    await _messageHandler.BroadcastMessageAsync(roomId, joinedMsg, except: socket);

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

        private async Task HandleMessageLoopAsync(WebSocket socket, string roomId, ClientInfo clientInfo)
        {
            var buffer = new byte[BufferSize];
            WebSocketReceiveResult? result = null;
            var lastPingTime = DateTime.UtcNow;

            try
            {
                while (!socket.CloseStatus.HasValue)
                {
                    var receiveTask = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    var delayTask = Task.Delay(PingInterval);

                    var completedTask = await Task.WhenAny(receiveTask, delayTask);

                    // Check for timeout
                    if (completedTask == delayTask)
                    {
                        if (DateTime.UtcNow - lastPingTime > TimeoutThreshold)
                        {
                            Console.WriteLine($"[{roomId}] Client {clientInfo.Id} timeout");
                            break;
                        }
                        continue;
                    }

                    result = await receiveTask;
                    lastPingTime = DateTime.UtcNow;

                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"[{roomId}] {clientInfo.Id}: {message}");

                    // Process message
                    await ProcessMessageAsync(socket, roomId, message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Socket error for {clientInfo.Id}: {ex}");
            }
            finally
            {
                // Close socket if needed
                try
                {
                    if (socket.State != WebSocketState.Closed && socket.State != WebSocketState.Aborted)
                    {
                        await socket.CloseAsync(result?.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result?.CloseStatusDescription ?? "Closed",
                            CancellationToken.None);
                    }
                }
                catch { /* Ignore close errors */ }
            }
        }

        private async Task ProcessMessageAsync(WebSocket socket, string roomId, string message)
        {
            var baseMessage = JsonSerializer.Deserialize<BaseMessage>(message);
            if (baseMessage == null) return;

            // Handle "who_is" query
            if (baseMessage.type == WhoIsMessage.MessageTypeConst)
            {
                await HandleWhoIsMessageAsync(socket, roomId, message);
                return;
            }

            // Broadcast other messages
            await _messageHandler.BroadcastMessageAsync(roomId, baseMessage, except: socket);
        }

        private async Task HandleWhoIsMessageAsync(WebSocket socket, string roomId, string message)
        {
            var whoIsMsg = JsonSerializer.Deserialize<WhoIsMessage>(message);
            if (whoIsMsg == null) return;

            var queriedClient = _roomManager.GetClient(roomId, whoIsMsg.queryClientId);
            if (queriedClient == null) return;

            var responseMsg = new PlayerJoinedMessage()
            {
                clientId = whoIsMsg.queryClientId,
                player = new Player()
                {
                    id = whoIsMsg.queryClientId,
                    name = queriedClient.Name
                }
            };

            await _messageHandler.SendAsync(socket, responseMsg);
        }

        private async Task CleanupClientAsync(WebSocket socket, string roomId, ClientInfo clientInfo)
        {
            _roomManager.RemoveClient(roomId, clientInfo.Id);
            Console.WriteLine($"[{roomId}] Client {clientInfo.Id} disconnected");

            // Notify other clients
            var leftMsg = new PlayerLeftMessage() { clientId = clientInfo.Id };
            await _messageHandler.BroadcastMessageAsync(roomId, leftMsg);
        }
    }

}
