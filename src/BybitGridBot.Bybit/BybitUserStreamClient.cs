using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BybitGridBot.Bybit;

public interface IBybitUserStreamClient
{
    Task RunAsync(Func<BybitUserStreamMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken);
}

public sealed class BybitUserStreamClient : IBybitUserStreamClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly string[] Topics = ["order", "execution", "position"];

    private readonly BybitOptions _options;
    private readonly BybitSigner _signer;
    private readonly ILogger<BybitUserStreamClient> _logger;

    public BybitUserStreamClient(
        IOptions<BybitOptions> options,
        BybitSigner signer,
        ILogger<BybitUserStreamClient> logger)
    {
        _options = options.Value;
        _signer = signer;
        _logger = logger;
    }

    public async Task RunAsync(Func<BybitUserStreamMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
    {
        EnsureCredentials();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                var endpoint = new Uri(_options.ResolvePrivateWebSocketUrl());
                await socket.ConnectAsync(endpoint, cancellationToken);
                _logger.LogInformation("Connected to Bybit private WebSocket: {Endpoint}", endpoint);

                await AuthenticateAsync(socket, cancellationToken);
                await SubscribeAsync(socket, cancellationToken);

                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeat = SendHeartbeatAsync(socket, heartbeatCts.Token);
                await ReceiveLoopAsync(socket, onMessage, cancellationToken);
                await heartbeatCts.CancelAsync();
                await heartbeat;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Bybit private WebSocket disconnected. Reconnecting in {DelaySeconds}s.", ReconnectDelay.TotalSeconds);
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
        }
    }

    private async Task AuthenticateAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds();
        var signature = _signer.Sign($"GET/realtime{expires}", _options.ApiSecret);
        await SendJsonAsync(socket, new { op = "auth", args = new object[] { _options.ApiKey, expires, signature } }, cancellationToken);
    }

    private static Task SubscribeAsync(ClientWebSocket socket, CancellationToken cancellationToken) =>
        SendJsonAsync(socket, new { op = "subscribe", args = Topics }, cancellationToken);

    private async Task SendHeartbeatAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PingInterval);
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (!await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }

            await SendJsonAsync(socket, new { op = "ping" }, cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        Func<BybitUserStreamMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var payload = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                payload.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var message = Encoding.UTF8.GetString(payload.ToArray());
            var streamMessage = ParseMessage(message);
            if (streamMessage is not null)
            {
                await onMessage(streamMessage, cancellationToken);
            }
        }
    }

    private BybitUserStreamMessage? ParseMessage(string message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (!root.TryGetProperty("topic", out var topicElement))
        {
            LogOperationMessage(root);
            return null;
        }

        var topic = topicElement.GetString() ?? string.Empty;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var creationTime = root.TryGetProperty("creationTime", out var creationTimeElement)
            ? ParseUnixMilliseconds(creationTimeElement)
            : DateTimeOffset.UtcNow;

        if (topic.StartsWith("order", StringComparison.OrdinalIgnoreCase))
        {
            return new BybitUserStreamMessage
            {
                Type = BybitUserStreamMessageType.Order,
                Topic = topic,
                CreationTime = creationTime,
                Orders = data.EnumerateArray().Select(ParseOrder).ToArray()
            };
        }

        if (topic.StartsWith("execution", StringComparison.OrdinalIgnoreCase))
        {
            return new BybitUserStreamMessage
            {
                Type = BybitUserStreamMessageType.Execution,
                Topic = topic,
                CreationTime = creationTime,
                Executions = data.EnumerateArray().Select(ParseExecution).ToArray()
            };
        }

        if (topic.StartsWith("position", StringComparison.OrdinalIgnoreCase))
        {
            return new BybitUserStreamMessage
            {
                Type = BybitUserStreamMessageType.Position,
                Topic = topic,
                CreationTime = creationTime,
                Positions = data.EnumerateArray().Select(ParsePosition).ToArray()
            };
        }

        return null;
    }

    private void LogOperationMessage(JsonElement root)
    {
        var op = root.TryGetProperty("op", out var opElement) ? opElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(op))
        {
            return;
        }

        if (string.Equals(op, "pong", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Bybit private WebSocket pong received.");
            return;
        }

        var success = root.TryGetProperty("success", out var successElement) && successElement.GetBoolean();
        if (success)
        {
            _logger.LogDebug("Bybit private WebSocket operation succeeded. Op: {Op}", op);
        }
        else
        {
            var message = root.TryGetProperty("ret_msg", out var retMsgElement) ? retMsgElement.GetString() : "unknown";
            _logger.LogWarning("Bybit private WebSocket operation failed. Op: {Op}. Message: {Message}", op, message);
        }
    }

    private static BybitOrderSnapshot ParseOrder(JsonElement item) => new()
    {
        OrderId = GetString(item, "orderId"),
        OrderLinkId = GetString(item, "orderLinkId"),
        Symbol = GetString(item, "symbol"),
        Side = GetString(item, "side"),
        OrderStatus = GetString(item, "orderStatus"),
        Price = GetDecimal(item, "price"),
        Quantity = GetDecimal(item, "qty"),
        CumExecQty = GetDecimal(item, "cumExecQty"),
        CumExecValue = GetDecimal(item, "cumExecValue"),
        AveragePrice = GetDecimal(item, "avgPrice"),
        FeePaid = GetDecimal(item, "cumExecFee"),
        ReduceOnly = GetBool(item, "reduceOnly"),
        PositionIdx = GetInt(item, "positionIdx"),
        CreatedAt = GetUnixMilliseconds(item, "createdTime"),
        UpdatedAt = GetUnixMilliseconds(item, "updatedTime")
    };

    private static BybitExecutionSnapshot ParseExecution(JsonElement item) => new()
    {
        ExecId = GetString(item, "execId"),
        OrderId = GetString(item, "orderId"),
        OrderLinkId = GetString(item, "orderLinkId"),
        Symbol = GetString(item, "symbol"),
        Side = GetString(item, "side"),
        ExecType = GetString(item, "execType"),
        ExecPrice = GetDecimal(item, "execPrice"),
        ExecQty = GetDecimal(item, "execQty"),
        ExecValue = GetDecimal(item, "execValue"),
        ExecFee = GetDecimal(item, "execFee"),
        ClosedSize = GetDecimal(item, "closedSize"),
        ExecPnl = GetDecimal(item, "execPnl"),
        IsMaker = GetBool(item, "isMaker"),
        ExecTime = GetUnixMilliseconds(item, "execTime")
    };

    private static BybitPositionSnapshot ParsePosition(JsonElement item) => new()
    {
        Symbol = GetString(item, "symbol"),
        Side = string.IsNullOrWhiteSpace(GetString(item, "side")) ? "None" : GetString(item, "side"),
        Size = GetDecimal(item, "size"),
        AveragePrice = GetDecimal(item, "entryPrice", "avgPrice"),
        MarkPrice = GetDecimal(item, "markPrice"),
        LiquidationPrice = GetDecimal(item, "liqPrice"),
        PositionValue = GetDecimal(item, "positionValue"),
        PositionInitialMargin = GetDecimal(item, "positionIM"),
        PositionMaintenanceMargin = GetDecimal(item, "positionMM"),
        Leverage = GetDecimal(item, "leverage"),
        UnrealizedPnl = GetDecimal(item, "unrealisedPnl"),
        RealizedPnl = GetDecimal(item, "cumRealisedPnl"),
        CurRealizedPnl = GetDecimal(item, "curRealisedPnl"),
        PositionStatus = string.IsNullOrWhiteSpace(GetString(item, "positionStatus")) ? "Normal" : GetString(item, "positionStatus"),
        PositionIdx = GetInt(item, "positionIdx"),
        TradeMode = GetInt(item, "tradeMode"),
        UpdatedAt = GetUnixMilliseconds(item, "updatedTime")
    };

    private static Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString() : string.Empty;

    private static decimal GetDecimal(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value))
            {
                continue;
            }

            var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0m;
    }

    private static int GetInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static bool GetBool(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static DateTimeOffset GetUnixMilliseconds(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? ParseUnixMilliseconds(value) : DateTimeOffset.UtcNow;

    private static DateTimeOffset ParseUnixMilliseconds(JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString();
        return long.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) && parsed > 0L
            ? DateTimeOffset.FromUnixTimeMilliseconds(parsed)
            : DateTimeOffset.UtcNow;
    }

    private void EnsureCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException("Bybit API credentials are required for private WebSocket.");
        }
    }
}

public enum BybitUserStreamMessageType
{
    Order,
    Execution,
    Position
}

public sealed class BybitUserStreamMessage
{
    public BybitUserStreamMessageType Type { get; init; }

    public string Topic { get; init; } = string.Empty;

    public DateTimeOffset CreationTime { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<BybitOrderSnapshot> Orders { get; init; } = [];

    public IReadOnlyList<BybitExecutionSnapshot> Executions { get; init; } = [];

    public IReadOnlyList<BybitPositionSnapshot> Positions { get; init; } = [];
}
