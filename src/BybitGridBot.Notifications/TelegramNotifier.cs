using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BybitGridBot.Notifications;

public interface ITelegramNotifier
{
    Task NotifyAsync(string message, CancellationToken cancellationToken);
}

public sealed class TelegramNotifier : ITelegramNotifier
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramNotifier> _logger;
    private readonly TelegramOptions _options;

    public TelegramNotifier(HttpClient httpClient, IOptions<TelegramOptions> options, ILogger<TelegramNotifier> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
    }

    public async Task NotifyAsync(string message, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.BotToken) || string.IsNullOrWhiteSpace(_options.ChatId))
        {
            _logger.LogWarning("Telegram notifications are enabled, but TELEGRAM_BOT_TOKEN or TELEGRAM_CHAT_ID is empty.");
            return;
        }

        var endpoint = $"bot{_options.BotToken}/sendMessage";
        var response = await _httpClient.PostAsJsonAsync(
            endpoint,
            new
            {
                chat_id = _options.ChatId,
                text = message,
                parse_mode = _options.ParseMode
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Telegram API returned status {StatusCode}: {ResponseBody}", response.StatusCode, body);
        }
    }
}
