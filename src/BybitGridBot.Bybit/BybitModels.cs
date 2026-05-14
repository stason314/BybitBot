using System.Globalization;
using System.Text.Json.Serialization;
using BybitGridBot.Domain;

namespace BybitGridBot.Bybit;

public interface IBybitRestClient
{
    Task<BybitTicker> GetTickerAsync(string category, string symbol, CancellationToken cancellationToken);
    Task<BybitWalletBalance> GetWalletBalanceAsync(CancellationToken cancellationToken, params string[] coins);
    Task<BybitFeeRate> GetFeeRateAsync(string category, string symbol, CancellationToken cancellationToken);
    Task<BybitOrderAck> CreateOrderAsync(BybitCreateOrderRequest request, CancellationToken cancellationToken);
    Task<BybitOrderAck> CancelOrderAsync(string category, string symbol, string? orderId, string? orderLinkId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BybitOrderSnapshot>> GetOpenOrdersAsync(string category, string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<BybitOrderSnapshot>> GetOrderHistoryAsync(string category, string symbol, string? orderLinkId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Candle>> GetKlinesAsync(string category, string symbol, string interval, int limit, CancellationToken cancellationToken);
    Task<BybitInstrumentInfo> GetInstrumentInfoAsync(string category, string symbol, CancellationToken cancellationToken);
}

public sealed class BybitApiException : Exception
{
    public BybitApiException(string message, long retCode, string retMsg)
        : base(message)
    {
        RetCode = retCode;
        RetMsg = retMsg;
    }

    public long RetCode { get; }

    public string RetMsg { get; }
}

public sealed class BybitCreateOrderRequest
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("side")]
    public string Side { get; init; } = string.Empty;

    [JsonPropertyName("orderType")]
    public string OrderType { get; init; } = "Limit";

    [JsonPropertyName("qty")]
    public string Qty { get; init; } = string.Empty;

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("timeInForce")]
    public string TimeInForce { get; init; } = "GTC";

    [JsonPropertyName("isLeverage")]
    public int IsLeverage { get; init; } = 0;

    [JsonPropertyName("orderLinkId")]
    public string OrderLinkId { get; init; } = string.Empty;
}

public sealed record BybitTicker(string Symbol, decimal LastPrice, decimal Bid1Price, decimal Ask1Price);

public sealed class BybitWalletBalance
{
    public decimal TotalAvailableBalance { get; init; }

    public IReadOnlyDictionary<string, BybitWalletCoin> Coins { get; init; } = new Dictionary<string, BybitWalletCoin>();

    public decimal GetCoinWalletBalance(string coin) =>
        Coins.TryGetValue(coin.ToUpperInvariant(), out var walletCoin) ? walletCoin.WalletBalance : 0m;

    public decimal GetCoinLockedBalance(string coin) =>
        Coins.TryGetValue(coin.ToUpperInvariant(), out var walletCoin) ? walletCoin.Locked : 0m;
}

public sealed record BybitWalletCoin(string Coin, decimal WalletBalance, decimal Locked, decimal Equity);

public sealed record BybitOrderAck(string OrderId, string OrderLinkId);

public sealed record BybitFeeRate(string Symbol, decimal MakerFeeRate, decimal TakerFeeRate);

public sealed class BybitOrderSnapshot
{
    public string OrderId { get; init; } = string.Empty;

    public string OrderLinkId { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public string Side { get; init; } = string.Empty;

    public string OrderStatus { get; init; } = string.Empty;

    public decimal Price { get; init; }

    public decimal Quantity { get; init; }

    public decimal CumExecQty { get; init; }

    public decimal CumExecValue { get; init; }

    public decimal AveragePrice { get; init; }

    public decimal FeePaid { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class BybitInstrumentInfo
{
    public string Symbol { get; init; } = string.Empty;

    public decimal TickSize { get; init; }

    public decimal QtyStep { get; init; }

    public decimal BasePrecision { get; init; }

    public decimal QuotePrecision { get; init; }

    public decimal MinOrderQty { get; init; }

    public decimal MinOrderAmount { get; init; }

    public decimal RoundPrice(decimal price) => RoundDown(price, TickSize);

    public decimal RoundQuantity(decimal quantity)
    {
        var step = QtyStep > 0m ? QtyStep : BasePrecision;
        return RoundDown(quantity, step);
    }

    private static decimal RoundDown(decimal value, decimal step)
    {
        if (step <= 0m)
        {
            return value;
        }

        return Math.Floor(value / step) * step;
    }
}

internal sealed class BybitEnvelope<T>
{
    [JsonPropertyName("retCode")]
    public long RetCode { get; init; }

    [JsonPropertyName("retMsg")]
    public string RetMsg { get; init; } = string.Empty;

    [JsonPropertyName("result")]
    public T? Result { get; init; }
}

internal sealed class BybitTickersResult
{
    [JsonPropertyName("list")]
    public List<BybitTickerItem> List { get; init; } = [];
}

internal sealed class BybitTickerItem
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("lastPrice")]
    public string LastPrice { get; init; } = "0";

    [JsonPropertyName("bid1Price")]
    public string Bid1Price { get; init; } = "0";

    [JsonPropertyName("ask1Price")]
    public string Ask1Price { get; init; } = "0";
}

internal sealed class BybitWalletBalanceResult
{
    [JsonPropertyName("list")]
    public List<BybitWalletAccountItem> List { get; init; } = [];
}

internal sealed class BybitWalletAccountItem
{
    [JsonPropertyName("totalAvailableBalance")]
    public string TotalAvailableBalance { get; init; } = "0";

    [JsonPropertyName("coin")]
    public List<BybitWalletCoinItem> Coins { get; init; } = [];
}

internal sealed class BybitWalletCoinItem
{
    [JsonPropertyName("coin")]
    public string Coin { get; init; } = string.Empty;

    [JsonPropertyName("walletBalance")]
    public string WalletBalance { get; init; } = "0";

    [JsonPropertyName("locked")]
    public string Locked { get; init; } = "0";

    [JsonPropertyName("equity")]
    public string Equity { get; init; } = "0";
}

internal sealed class BybitOrderAckResult
{
    [JsonPropertyName("orderId")]
    public string OrderId { get; init; } = string.Empty;

    [JsonPropertyName("orderLinkId")]
    public string OrderLinkId { get; init; } = string.Empty;
}

internal sealed class BybitOrdersResult
{
    [JsonPropertyName("list")]
    public List<BybitOrderItem> List { get; init; } = [];
}

internal sealed class BybitFeeRateResult
{
    [JsonPropertyName("list")]
    public List<BybitFeeRateItem> List { get; init; } = [];
}

internal sealed class BybitFeeRateItem
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("makerFeeRate")]
    public string MakerFeeRate { get; init; } = "0";

    [JsonPropertyName("takerFeeRate")]
    public string TakerFeeRate { get; init; } = "0";
}

internal sealed class BybitOrderItem
{
    [JsonPropertyName("orderId")]
    public string OrderId { get; init; } = string.Empty;

    [JsonPropertyName("orderLinkId")]
    public string OrderLinkId { get; init; } = string.Empty;

    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("side")]
    public string Side { get; init; } = string.Empty;

    [JsonPropertyName("orderStatus")]
    public string OrderStatus { get; init; } = string.Empty;

    [JsonPropertyName("price")]
    public string Price { get; init; } = "0";

    [JsonPropertyName("qty")]
    public string Qty { get; init; } = "0";

    [JsonPropertyName("cumExecQty")]
    public string CumExecQty { get; init; } = "0";

    [JsonPropertyName("cumExecValue")]
    public string CumExecValue { get; init; } = "0";

    [JsonPropertyName("avgPrice")]
    public string AvgPrice { get; init; } = "0";

    [JsonPropertyName("cumExecFee")]
    public string CumExecFee { get; init; } = "0";

    [JsonPropertyName("createdTime")]
    public string CreatedTime { get; init; } = "0";

    [JsonPropertyName("updatedTime")]
    public string UpdatedTime { get; init; } = "0";

    [JsonPropertyName("cumFeeDetail")]
    public Dictionary<string, string>? CumFeeDetail { get; init; }
}

internal sealed class BybitKlineResult
{
    [JsonPropertyName("list")]
    public List<List<string>> List { get; init; } = [];
}

internal sealed class BybitInstrumentsResult
{
    [JsonPropertyName("list")]
    public List<BybitInstrumentItem> List { get; init; } = [];
}

internal sealed class BybitInstrumentItem
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("priceFilter")]
    public BybitPriceFilter PriceFilter { get; init; } = new();

    [JsonPropertyName("lotSizeFilter")]
    public BybitLotSizeFilter LotSizeFilter { get; init; } = new();
}

internal sealed class BybitPriceFilter
{
    [JsonPropertyName("tickSize")]
    public string TickSize { get; init; } = "0";
}

internal sealed class BybitLotSizeFilter
{
    [JsonPropertyName("qtyStep")]
    public string QtyStep { get; init; } = "0";

    [JsonPropertyName("basePrecision")]
    public string BasePrecision { get; init; } = "0";

    [JsonPropertyName("quotePrecision")]
    public string QuotePrecision { get; init; } = "0";

    [JsonPropertyName("minOrderQty")]
    public string MinOrderQty { get; init; } = "0";

    [JsonPropertyName("minOrderAmt")]
    public string MinOrderAmt { get; init; } = "0";

    [JsonPropertyName("minNotionalValue")]
    public string MinNotionalValue { get; init; } = "0";
}

internal static class BybitModelMapper
{
    public static decimal ParseDecimal(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    public static DateTimeOffset ParseUnixMilliseconds(string value)
    {
        return long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? DateTimeOffset.FromUnixTimeMilliseconds(parsed)
            : DateTimeOffset.UnixEpoch;
    }

    public static BybitOrderSnapshot ToSnapshot(this BybitOrderItem item)
    {
        decimal fee = ParseDecimal(item.CumExecFee);
        if (fee <= 0m && item.CumFeeDetail is { Count: > 0 })
        {
            fee = item.CumFeeDetail.Values.Sum(ParseDecimal);
        }

        return new BybitOrderSnapshot
        {
            OrderId = item.OrderId,
            OrderLinkId = item.OrderLinkId,
            Symbol = item.Symbol,
            Side = item.Side,
            OrderStatus = item.OrderStatus,
            Price = ParseDecimal(item.Price),
            Quantity = ParseDecimal(item.Qty),
            CumExecQty = ParseDecimal(item.CumExecQty),
            CumExecValue = ParseDecimal(item.CumExecValue),
            AveragePrice = ParseDecimal(item.AvgPrice),
            FeePaid = fee,
            CreatedAt = ParseUnixMilliseconds(item.CreatedTime),
            UpdatedAt = ParseUnixMilliseconds(item.UpdatedTime)
        };
    }
}
