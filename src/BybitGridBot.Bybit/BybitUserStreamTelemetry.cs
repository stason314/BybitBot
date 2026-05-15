namespace BybitGridBot.Bybit;

public sealed class BybitUserStreamTelemetry
{
    private readonly object _gate = new();

    private BybitUserStreamTelemetrySnapshot _snapshot = new();

    public void MarkConnected(string endpoint)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                IsConnected = true,
                Endpoint = endpoint,
                ConnectedAt = DateTimeOffset.UtcNow,
                LastError = null
            };
        }
    }

    public void MarkDisconnected(Exception? exception)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                IsConnected = false,
                DisconnectCount = _snapshot.DisconnectCount + 1,
                LastError = exception?.Message
            };
        }
    }

    public void MarkMessage(string topic)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastMessageAt = DateTimeOffset.UtcNow,
                LastTopic = topic
            };
        }
    }

    public void MarkHandled(BybitUserStreamMessageType type, string topic)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastEventAt = DateTimeOffset.UtcNow,
                LastEventType = type.ToString(),
                LastTopic = topic
            };
        }
    }

    public BybitUserStreamTelemetrySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }
}

public sealed record BybitUserStreamTelemetrySnapshot
{
    public bool IsConnected { get; init; }

    public string? Endpoint { get; init; }

    public DateTimeOffset? ConnectedAt { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    public DateTimeOffset? LastEventAt { get; init; }

    public string? LastEventType { get; init; }

    public string? LastTopic { get; init; }

    public int DisconnectCount { get; init; }

    public string? LastError { get; init; }
}
