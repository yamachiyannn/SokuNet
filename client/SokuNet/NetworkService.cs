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

internal sealed record FriendPresence(
    string PlayerId,
    string DisplayName,
    string GameStatus,
    bool IsAway,
    bool IsPrivate);

internal enum NotificationCategory
{
    Friend,
    Announcement,
    System
}

internal sealed record RemoteNotification(string Message, NotificationCategory Category = NotificationCategory.System);

/// <summary>
/// WebSocket relay client. The first connection is labelled host by the relay;
/// Cloudflare, not the host PC, carries the network traffic.
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
    private readonly string _installationId;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentQueue<RemoteNotification> _notifications = new();
    private readonly ConcurrentDictionary<string, FriendPresence> _friends = new(StringComparer.Ordinal);
    private readonly object _presenceLock = new();
    private LocalPresence _currentPresence;
    private bool _hasPresence;
    // Presence changes during the WebSocket handshake are retained locally and
    // flushed immediately after the initial hello/welcome exchange begins.
    private int _initialHandshakeReady;
    private ClientWebSocket? _socket;
    private string _connectionStatus = "未設定";
    private string _roleText = "未接続";
    private bool _disposed;

    public string ConnectionStatus => Volatile.Read(ref _connectionStatus);
    public string RoleText => Volatile.Read(ref _roleText);

    public NetworkService(AppSettings settings, LocalPresence initialPresence)
    {
        _shutdownToken = _shutdown.Token;
        _roomCode = settings.RoomCode?.Trim() ?? string.Empty;
        _displayName = CleanText(settings.DisplayName, 24);
        _installationId = Guid.TryParseExact(settings.InstallationId, "N", out _)
            ? settings.InstallationId
            : Guid.NewGuid().ToString("N");
        _currentPresence = initialPresence with { DisplayName = _displayName };
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
        else
        {
            _connectionStatus = !string.IsNullOrWhiteSpace(_roomCode) && !IsSafeRoomCode(_roomCode)
                ? "合言葉設定エラー"
                : "通信設定不足";
        }
    }

    public IReadOnlyCollection<FriendPresence> GetFriends() => _friends.Values
        .OrderBy(friend => friend.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsSafeRoomCode(string value) =>
        value.Length >= 16 && value.All(character =>
            (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z') ||
            (character >= '0' && character <= '9') || character is '-' or '_');

    public void UpdatePresence(LocalPresence presence)
    {
        bool changed;
        lock (_presenceLock)
        {
            changed = !_hasPresence ||
                      _currentPresence.IsPrivate != presence.IsPrivate ||
                      (!presence.IsPrivate && _currentPresence != presence);
            _currentPresence = presence;
            _hasPresence = true;
        }

        if (changed && !_disposed && Volatile.Read(ref _initialHandshakeReady) != 0)
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
            Volatile.Write(ref _initialHandshakeReady, 0);
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
                Volatile.Write(ref _initialHandshakeReady, 1);

                // Flush the newest state in case it changed while ConnectAsync or
                // the initial hello was in flight. This avoids sending "presence"
                // before "hello" and losing the first roster handshake.
                await SendCurrentPresenceAsync(isInitial: false, _shutdownToken).ConfigureAwait(false);
                await ReceiveLoopAsync(socket, _shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Network failure is non-fatal: local detection and the CMD UI continue.
            }
            finally
            {
                if (ReferenceEquals(_socket, socket))
                    _socket = null;
                Volatile.Write(ref _initialHandshakeReady, 0);
                Volatile.Write(ref _roleText, "未接続");
                _friends.Clear();
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

        // Even private participants identify themselves to their invited group so
        // that friends can show the row as "非公開". No game state is sent.
        object message;
        if (presence.IsPrivate)
        {
            message = new ClientMessage
            {
                Type = isInitial ? "hello" : "hide",
                DisplayName = _displayName,
                InstallationId = isInitial ? _installationId : null,
                IsPrivate = true
            };
        }
        else
        {
            message = new ClientMessage
            {
                Type = isInitial ? "hello" : "presence",
                DisplayName = _displayName,
                InstallationId = isInitial ? _installationId : null,
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
                    UpdateRole(root);
                    ReplaceFriendSnapshot(root);
                    break;

                case "role":
                    UpdateRole(root);
                    break;

                case "peer_joined":
                    UpsertFriend(root, notify: false);
                    break;

                case "peer_state":
                    UpsertFriend(root, notify: true);
                    break;

                case "peer_private":
                    UpsertFriend(root, notify: false);
                    break;

                case "peer_left":
                    if (root.TryGetProperty("playerId", out JsonElement leftId))
                        _friends.TryRemove(leftId.GetString() ?? string.Empty, out _);
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

    private void UpdateRole(JsonElement root)
    {
        if (!root.TryGetProperty("role", out JsonElement role))
            return;

        string roleValue = role.GetString() ?? string.Empty;
        Volatile.Write(ref _roleText, roleValue switch
        {
            "host" => "ホスト",
            "client" => "クライアント",
            _ => "未接続"
        });
    }

    private void ReplaceFriendSnapshot(JsonElement root)
    {
        _friends.Clear();
        if (!root.TryGetProperty("peers", out JsonElement peers) || peers.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement peer in peers.EnumerateArray())
            UpsertFriend(peer, notify: false);
    }

    private void UpsertFriend(JsonElement root, bool notify)
    {
        string playerId = ReadString(root, "playerId", 80);
        if (playerId.Length == 0)
            return;

        string name = ReadString(root, "displayName", 24);
        if (name.Length == 0)
            name = "参加者";
        bool isPrivate = root.TryGetProperty("isPrivate", out JsonElement privateElement) &&
                         privateElement.ValueKind == JsonValueKind.True;
        string status = isPrivate ? string.Empty : ReadString(root, "gameStatus", 24);
        bool isAway = !isPrivate && root.TryGetProperty("isAway", out JsonElement awayElement) &&
                      awayElement.ValueKind == JsonValueKind.True;

        var next = new FriendPresence(playerId, name, status, isAway, isPrivate);
        _friends.TryGetValue(playerId, out FriendPresence? previous);
        _friends[playerId] = next;

        if (!notify || previous is null || next.IsPrivate || previous.IsPrivate)
            return;

        if (previous.GameStatus == "未起動" && next.GameStatus != "未起動")
        {
            string startedState = next.GameStatus == "取得不可" ? "状態取得中" : FormatState(next);
            _notifications.Enqueue(new RemoteNotification($"{name} が東方非想天則を始めました（{startedState}）", NotificationCategory.Friend));
        }
        else if (next.GameStatus == "未起動" &&
                 previous.GameStatus is not ("未起動" or "取得不可"))
        {
            _notifications.Enqueue(new RemoteNotification($"{name}：天則を終了しました", NotificationCategory.Friend));
        }
        else if (previous.GameStatus != "取得不可" && previous.GameStatus != next.GameStatus)
        {
            _notifications.Enqueue(new RemoteNotification($"{name}：{FormatState(next)}", NotificationCategory.Friend));
        }
        else if (previous.IsAway != next.IsAway)
        {
            _notifications.Enqueue(new RemoteNotification(next.IsAway
                ? $"{name}：離席中になりました"
                : $"{name}：戻りました", NotificationCategory.Friend));
        }
    }

    private static string FormatState(FriendPresence friend) => friend.IsAway
        ? "離席中"
        : string.IsNullOrWhiteSpace(friend.GameStatus) ? "状態取得中" : friend.GameStatus;

    private static string ReadString(JsonElement root, string propertyName, int maxLength)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element) || element.ValueKind != JsonValueKind.String)
            return string.Empty;
        return CleanText(element.GetString(), maxLength);
    }

    private static string CleanText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string cleaned = new(value.Where(character => !char.IsControl(character)).ToArray());
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
    }

    private sealed class ClientMessage
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
        [JsonPropertyName("installationId")] public string? InstallationId { get; init; }
        [JsonPropertyName("gameStatus")] public string? GameStatus { get; init; }
        [JsonPropertyName("isAway")] public bool? IsAway { get; init; }
        [JsonPropertyName("isPrivate")] public bool? IsPrivate { get; init; }
    }
}
