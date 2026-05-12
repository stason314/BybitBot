using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.Notifications;

public sealed class TelegramOptions
{
    [ConfigurationKeyName("TELEGRAM_ENABLED")]
    public bool Enabled { get; init; }

    [ConfigurationKeyName("TELEGRAM_BOT_TOKEN")]
    public string BotToken { get; init; } = string.Empty;

    [ConfigurationKeyName("TELEGRAM_CHAT_ID")]
    public string ChatId { get; init; } = string.Empty;

    [ConfigurationKeyName("TELEGRAM_PARSE_MODE")]
    public string ParseMode { get; init; } = "Markdown";
}
