using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SokuNet;

internal readonly record struct LocalPresence(
    string DisplayName,
    string GameStatus,
    bool IsAway,
    bool IsPrivate);

internal sealed record RemoteNotification(string PlayerName, string StateText);

/// <summary>
/// Small reconnecting WebSocket client. The Cloudflare Durable Object is the room
/// coordinator/relay; the first connected member is labelled host and later members client.
/// </summary>
internal sealed class NetworkService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Uri? _endpoint;
    private readonly string _roomCode;
    private readonly string _displayName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentQueue<RemoteNotification> _notifications = new();
    private readonly object _presenceLock = new();
    private LocalPresence _currentPresence;
    private bool _hasPresence;
    private ClientWebSocket? _socket;
    private string _connectionStatus = "未設定";
    private string _roleText = "未接続";
    private bool _disposed;

    public string ConnectionStatus => Volatile.Read(ref _connectionStatus);
    public string RoleText => Volatile.Read(ref _roleText);

    public NetworkService(AppSettings settings)
    {
        _shutdownToken = _shutdown.Token;
        _roomCode = settings.RoomCode?.Trim() ?? string.Empty;
        _displayName = CleanText(settings.DisplayName, 24);
        _currentPresence = new LocalPresence(_displayName, "未起動", false, settings.PrivateMode);
        _hasPresence = true;

        if (!string.IsNullOrWhiteSpace(settings.RelayUrl) && IsSafeRoomCode(_roomCode) && _displayName.Length > 0)
        {
            try
            {
                _endpoint = BuildWebSocketUri(settings.RelayUrl);
                _connectionStatus = "接続中";
                _ = Task.Run(RunConnectionLoopAsync);
            }
            catch (UriFormatException)
            {
                _connectionStatus = "URL設定エラー";
            }
        }
        else if (!string.IsNullOrWhiteSpace(settings.RelayUrl) || !string.IsNullOrWhiteSpace(_roomCode))
        {
            _connectionStatus = IsSafeRoomCode(_roomCode) ? "通信設定不足" : "合言葉設定エラー";
        }
    }

    private static bool IsSafeRoomCode(string value) =>
        value.Length >= 16 && value.All(c =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') || c is '-' or '_');

    public void UpdatePresence(LocalPresence presence)
    {
        bool changed;
        lock (_presenceLock)
        {
            changed = !_hasPresence || _currentPresence != presence;
            _currentPresence = presence;
            _hasPresence = true;
        }

        if (changed && !_disposed)
            _ = SendCurrentPresenceQuietlyAsync(isInitial: false, _shutdownToken);
    }

    public bool TryDequeueNotification(out RemoteNotification? notification) =>
        _notifications.TryDequeue(out notification);

    private async Task RunConnectionLoopAsync()
    {
        int retrySeconds = 2;
        while (!_shutdown.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            try
            {
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
                socket.Options.SetRequestHeader("Authorization", $"Bearer {_roomCode}");
                Volatile.Write(ref _connectionStatus, "接続中");
                await socket.ConnectAsync(_endpoint!, _shutdownToken).ConfigureAwait(false);
                _socket = socket;
                retrySeconds = 2;
                Volatile.Write(ref _connectionStatus, "接続済み");
                await SendCurrentPresenceAsync(isInitial: true, _shutdownToken).ConfigureAwait(false);
                await ReceiveLoopAsync(socket, _shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Network failure is non-fatal: local game detection continues and reconnects.
            }
            finally
            {
                if (ReferenceEquals(_socket, socket))
                    _socket = null;
                Volatile.Write(ref _roleText, "未接続");
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try { socket.Abort(); } catch { }
                }
            }

            if (_shutdown.IsCancellationRequested)
                break;

            Volatile.Write(ref _connectionStatus, "再接続中");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), _shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            retrySeconds = Math.Min(30, retrySeconds * 2);
        }

        Volatile.Write(ref _connectionStatus, "未接続");
    }

    private async Task SendCurrentPresenceAsync(bool isInitial, CancellationToken cancellationToken)
    {
        LocalPresence presence;
        lock (_presenceLock)
        {
            if (!_hasPresence)
                return;
            presence = _currentPresence;
        }

        object message;
        if (presence.IsPrivate)
        {
            message = new ClientMessage { Type = isInitial ? "hello" : "hide", IsPrivate = true };
        }
        else
        {
            message = new ClientMessage
            {
                Type = isInitial ? "hello" : "presence",
                DisplayName = _displayName,
                GameStatus = presence.GameStatus,
                IsAway = presence.IsAway,
                IsPrivate = false
            };
        }

        await SendJsonAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCurrentPresenceQuietlyAsync(bool isInitial, CancellationToken cancellationToken)
    {
        try
        {
            await SendCurrentPresenceAsync(isInitial, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception) { }
    }

    private async Task SendJsonAsync(object message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClientWebSocket? socket = _socket;
            if (socket?.State != WebSocketState.Open)
                return;

            byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
            await socket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        using var messageBuffer = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            messageBuffer.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                if (messageBuffer.Length + result.Count > 32 * 1024)
                    throw new WebSocketException("Server message exceeded the client limit.");
                messageBuffer.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (messageBuffer.Length == 0)
                continue;
            HandleServerMessage(Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length));
        }
    }

    private void HandleServerMessage(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement))
                return;

            string type = typeElement.GetString() ?? string.Empty;
            switch (type)
            {
                case "welcome":
                case "role":
                    if (root.TryGetProperty("role", out JsonElement role))
                    {
                        string roleValue = role.GetString() ?? "";
                        Volatile.Write(ref _roleText, roleValue switch
                        {
                            "host" => "ホスト",
                            "client" => "クライアント",
                            _ => "未接続"
                        });
                    }
                    break;

                case "peer_joined":
                    EnqueuePeerEvent(root, "オンライン");
                    break;

                case "peer_state":
                    EnqueuePeerEvent(root, null);
                    break;

                case "peer_left":
                    EnqueuePeerEvent(root, "オフライン");
                    break;

                case "error":
                    Volatile.Write(ref _connectionStatus, "サーバー応答エラー");
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed frames; they must not crash the tray application.
        }
    }

    private void EnqueuePeerEvent(JsonElement root, string? suffix)
    {
        string name = root.TryGetProperty("displayName", out JsonElement nameElement)
            ? CleanText(nameElement.GetString(), 24)
            : "参加者";
        if (name.Length == 0)
            name = "参加者";

        string status = root.TryGetProperty("gameStatus", out JsonElement statusElement)
            ? CleanText(statusElement.GetString(), 24)
            : string.Empty;
        bool away = root.TryGetProperty("isAway", out JsonElement awayElement) && awayElement.ValueKind == JsonValueKind.True;

        string stateText = suffix ?? status;
        if (suffix == "オンライン" && status.Length > 0)
            stateText = $"オンライン・{status}";
        if (away && suffix != "オフライン")
            stateText += "・離席中";
        if (stateText.Length == 0)
            stateText = "状態が更新されました";

        _notifications.Enqueue(new RemoteNotification(name, stateText));
    }

    private static string CleanText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string cleaned = new(value.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static Uri BuildWebSocketUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
            throw new UriFormatException("Relay URL must be absolute.");

        string scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "https" => "wss",
            "http" => "ws",
            "wss" => "wss",
            "ws" => "ws",
            _ => throw new UriFormatException("Relay URL must use HTTPS or WSS.")
        };

        var builder = new UriBuilder(uri) { Scheme = scheme };
        if (builder.Port == 443 && scheme == "wss")
            builder.Port = -1;
        string path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
            path += "/ws";
        builder.Path = path;
        return builder.Uri;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        try { _socket?.Abort(); } catch { }
        _socket?.Dispose();
        // The shutdown token remains valid for the short-lived background task to unwind safely.
    }

    private sealed class ClientMessage
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
        [JsonPropertyName("gameStatus")] public string? GameStatus { get; init; }
        [JsonPropertyName("isAway")] public bool? IsAway { get; init; }
        [JsonPropertyName("isPrivate")] public bool? IsPrivate { get; init; }
    }
}
