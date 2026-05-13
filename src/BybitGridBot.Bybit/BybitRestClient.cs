using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BybitGridBot.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BybitGridBot.Bybit;

public sealed class BybitRestClient : IBybitRestClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<long> TransientRetCodes = [10000, 10006, 10016, 10019];

    private readonly HttpClient _httpClient;
    private readonly ILogger<BybitRestClient> _logger;
    private readonly BybitOptions _options;
    private readonly BybitSigner _signer;

    public BybitRestClient(
        HttpClient httpClient,
        IOptions<BybitOptions> options,
        BybitSigner signer,
        ILogger<BybitRestClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
        _signer = signer;
    }

    public async Task<BybitTicker> GetTickerAsync(string category, string symbol, CancellationToken cancellationToken)
    {
        var result = await SendAsync<BybitTickersResult>(
            HttpMethod.Get,
            "/v5/market/tickers",
            false,
            new Dictionary<string, string?>
            {
                ["category"] = category,
                ["symbol"] = symbol
            },
            null,
            cancellationToken);

        var ticker = result.List.SingleOrDefault()
            ?? throw new InvalidOperationException($"Ticker response for {symbol} is empty.");

        return new BybitTicker(
            ticker.Symbol,
            BybitModelMapper.ParseDecimal(ticker.LastPrice),
            BybitModelMapper.ParseDecimal(ticker.Bid1Price),
            BybitModelMapper.ParseDecimal(ticker.Ask1Price));
    }

    public async Task<BybitWalletBalance> GetWalletBalanceAsync(CancellationToken cancellationToken, params string[] coins)
    {
        var coinFilter = coins.Length > 0 ? string.Join(',', coins.Where(static coin => !string.IsNullOrWhiteSpace(coin))) : null;
        var result = await SendAsync<BybitWalletBalanceResult>(
            HttpMethod.Get,
            "/v5/account/wallet-balance",
            true,
            new Dictionary<string, string?>
            {
                ["accountType"] = _options.AccountType,
                ["coin"] = coinFilter
            },
            null,
            cancellationToken);

        var account = result.List.FirstOrDefault() ?? new BybitWalletAccountItem();
        var coinsMap = account.Coins.ToDictionary(
            item => item.Coin.ToUpperInvariant(),
            item => new BybitWalletCoin(
                item.Coin,
                BybitModelMapper.ParseDecimal(item.WalletBalance),
                BybitModelMapper.ParseDecimal(item.Locked),
                BybitModelMapper.ParseDecimal(item.Equity)));

        return new BybitWalletBalance
        {
            TotalAvailableBalance = BybitModelMapper.ParseDecimal(account.TotalAvailableBalance),
            Coins = coinsMap
        };
    }

    public async Task<BybitOrderAck> CreateOrderAsync(BybitCreateOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await SendAsync<BybitOrderAckResult>(
            HttpMethod.Post,
            "/v5/order/create",
            true,
            null,
            request,
            cancellationToken);

        return new BybitOrderAck(result.OrderId, result.OrderLinkId);
    }

    public async Task<BybitOrderAck> CancelOrderAsync(
        string category,
        string symbol,
        string? orderId,
        string? orderLinkId,
        CancellationToken cancellationToken)
    {
        var result = await SendAsync<BybitOrderAckResult>(
            HttpMethod.Post,
            "/v5/order/cancel",
            true,
            null,
            new
            {
                category,
                symbol,
                orderId,
                orderLinkId
            },
            cancellationToken);

        return new BybitOrderAck(result.OrderId, result.OrderLinkId);
    }

    public async Task<IReadOnlyList<BybitOrderSnapshot>> GetOpenOrdersAsync(string category, string symbol, CancellationToken cancellationToken)
    {
        var result = await SendAsync<BybitOrdersResult>(
            HttpMethod.Get,
            "/v5/order/realtime",
            true,
            new Dictionary<string, string?>
            {
                ["category"] = category,
                ["symbol"] = symbol,
                ["openOnly"] = "0",
                ["limit"] = "50"
            },
            null,
            cancellationToken);

        return result.List.Select(static item => item.ToSnapshot()).ToArray();
    }

    public async Task<IReadOnlyList<BybitOrderSnapshot>> GetOrderHistoryAsync(
        string category,
        string symbol,
        string? orderLinkId,
        CancellationToken cancellationToken)
    {
        var result = await SendAsync<BybitOrdersResult>(
            HttpMethod.Get,
            "/v5/order/history",
            true,
            new Dictionary<string, string?>
            {
                ["category"] = category,
                ["symbol"] = symbol,
                ["orderLinkId"] = orderLinkId,
                ["limit"] = "50"
            },
            null,
            cancellationToken);

        return result.List.Select(static item => item.ToSnapshot()).ToArray();
    }

    public async Task<IReadOnlyList<Candle>> GetKlinesAsync(
        string category,
        string symbol,
        string interval,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = await SendAsync<BybitKlineResult>(
            HttpMethod.Get,
            "/v5/market/kline",
            false,
            new Dictionary<string, string?>
            {
                ["category"] = category,
                ["symbol"] = symbol,
                ["interval"] = interval,
                ["limit"] = limit.ToString()
            },
            null,
            cancellationToken);

        return result.List
            .Select(static row => new Candle(
                BybitModelMapper.ParseUnixMilliseconds(row[0]),
                BybitModelMapper.ParseDecimal(row[1]),
                BybitModelMapper.ParseDecimal(row[2]),
                BybitModelMapper.ParseDecimal(row[3]),
                BybitModelMapper.ParseDecimal(row[4]),
                BybitModelMapper.ParseDecimal(row[5]),
                BybitModelMapper.ParseDecimal(row[6])))
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
    }

    public async Task<BybitInstrumentInfo> GetInstrumentInfoAsync(string category, string symbol, CancellationToken cancellationToken)
    {
        var result = await SendAsync<BybitInstrumentsResult>(
            HttpMethod.Get,
            "/v5/market/instruments-info",
            false,
            new Dictionary<string, string?>
            {
                ["category"] = category,
                ["symbol"] = symbol
            },
            null,
            cancellationToken);

        var item = result.List.SingleOrDefault()
            ?? throw new InvalidOperationException($"Instrument info for {symbol} is empty.");

        return new BybitInstrumentInfo
        {
            Symbol = item.Symbol,
            TickSize = BybitModelMapper.ParseDecimal(item.PriceFilter.TickSize),
            QtyStep = BybitModelMapper.ParseDecimal(item.LotSizeFilter.QtyStep),
            BasePrecision = BybitModelMapper.ParseDecimal(item.LotSizeFilter.BasePrecision),
            QuotePrecision = BybitModelMapper.ParseDecimal(item.LotSizeFilter.QuotePrecision),
            MinOrderQty = BybitModelMapper.ParseDecimal(item.LotSizeFilter.MinOrderQty),
            MinOrderAmount = decimal.Max(
                BybitModelMapper.ParseDecimal(item.LotSizeFilter.MinOrderAmt),
                BybitModelMapper.ParseDecimal(item.LotSizeFilter.MinNotionalValue))
        };
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        bool signed,
        IReadOnlyDictionary<string, string?>? query,
        object? body,
        CancellationToken cancellationToken)
    {
        var attempts = 0;
        Exception? lastException = null;

        while (attempts < _options.RetryCount)
        {
            attempts++;

            try
            {
                using var request = CreateRequest(method, path, signed, query, body);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (ShouldRetry(response.StatusCode))
                {
                    throw new HttpRequestException(
                        $"Bybit returned transient status {(int)response.StatusCode}: {responseBody}",
                        null,
                        response.StatusCode);
                }

                response.EnsureSuccessStatusCode();
                var envelope = JsonSerializer.Deserialize<BybitEnvelope<T>>(responseBody, SerializerOptions)
                    ?? throw new InvalidOperationException("Bybit response deserialization returned null.");

                if (envelope.RetCode != 0)
                {
                    if (TransientRetCodes.Contains(envelope.RetCode))
                    {
                        throw new BybitApiException(
                            $"Transient Bybit error {envelope.RetCode}: {envelope.RetMsg}",
                            envelope.RetCode,
                            envelope.RetMsg);
                    }

                    throw new BybitApiException(
                        $"Bybit error {envelope.RetCode}: {envelope.RetMsg}",
                        envelope.RetCode,
                        envelope.RetMsg);
                }

                return envelope.Result ?? throw new InvalidOperationException("Bybit result payload is null.");
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken) && attempts < _options.RetryCount)
            {
                lastException = exception;
                var delay = TimeSpan.FromSeconds(Math.Min(attempts * attempts, 10));
                _logger.LogWarning(exception, "Transient Bybit API error. Attempt {Attempt} of {RetryCount}.", attempts, _options.RetryCount);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("Bybit request failed.");
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        bool signed,
        IReadOnlyDictionary<string, string?>? query,
        object? body)
    {
        var queryString = BuildQueryString(query);
        var relativeUri = string.IsNullOrWhiteSpace(queryString) ? path : $"{path}?{queryString}";
        var baseUrl = signed ? _options.ResolvePrivateBaseUrl() : _options.ResolvePublicBaseUrl();
        var request = new HttpRequestMessage(method, new Uri(new Uri($"{baseUrl}/"), relativeUri.TrimStart('/')));
        var bodyJson = body is null ? string.Empty : JsonSerializer.Serialize(body, SerializerOptions);

        if (method != HttpMethod.Get && body is not null)
        {
            request.Content = JsonContent.Create(body, options: SerializerOptions);
        }

        if (!signed)
        {
            return request;
        }

        EnsureCredentials();

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var payload = method == HttpMethod.Get
            ? $"{timestamp}{_options.ApiKey}{_options.RecvWindow}{queryString}"
            : $"{timestamp}{_options.ApiKey}{_options.RecvWindow}{bodyJson}";
        var signature = _signer.Sign(payload, _options.ApiSecret);

        request.Headers.Add("X-BAPI-API-KEY", _options.ApiKey);
        request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
        request.Headers.Add("X-BAPI-SIGN", signature);
        request.Headers.Add("X-BAPI-RECV-WINDOW", _options.RecvWindow.ToString());

        return request;
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        return exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => false,
            HttpRequestException => true,
            BybitApiException bybitApiException when TransientRetCodes.Contains(bybitApiException.RetCode) => true,
            _ => false
        };
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string?>? query)
    {
        if (query is null || query.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var pair in query.Where(static pair => !string.IsNullOrWhiteSpace(pair.Value)))
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(pair.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(pair.Value!));
        }

        return builder.ToString();
    }

    private void EnsureCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException("Bybit API credentials are required for private endpoints.");
        }
    }
}
