using System;
using Newtonsoft.Json;

namespace CodexUsageMonitor.Models
{
    public sealed class ClaudeCredentialsFile
    {
        [JsonProperty("claudeAiOauth")]
        public ClaudeOauthTokens ClaudeAiOauth { get; set; }
    }

    public sealed class ClaudeOauthTokens
    {
        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }

        [JsonProperty("subscriptionType")]
        public string SubscriptionType { get; set; }
    }

    public sealed class ClaudeUsageResponse
    {
        [JsonProperty("five_hour")]
        public ClaudeUsageBucket FiveHour { get; set; }

        [JsonProperty("seven_day")]
        public ClaudeUsageBucket SevenDay { get; set; }

        [JsonProperty("seven_day_sonnet")]
        public ClaudeUsageBucket SevenDaySonnet { get; set; }

        [JsonProperty("seven_day_opus")]
        public ClaudeUsageBucket SevenDayOpus { get; set; }
    }

    public sealed class ClaudeUsageBucket
    {
        [JsonProperty("utilization")]
        public double? Utilization { get; set; }

        [JsonProperty("resets_at")]
        public DateTimeOffset? ResetsAt { get; set; }
    }

    public sealed class ClaudeUsageSnapshot
    {
        public string SubscriptionType { get; set; } = "-";
        public double FiveHourUtilization { get; set; } = -1;
        public DateTime? FiveHourResetUtc { get; set; }
        public double SevenDayUtilization { get; set; } = -1;
        public DateTime? SevenDayResetUtc { get; set; }
        public double SevenDaySonnetUtilization { get; set; } = -1;
        public double SevenDayOpusUtilization { get; set; } = -1;
        public DateTime FetchedAtUtc { get; set; }
        public string Error { get; set; }
        public bool Unauthorized { get; set; }

        public bool HasData =>
            string.IsNullOrWhiteSpace(Error) &&
            (FiveHourResetUtc.HasValue || SevenDayResetUtc.HasValue);
    }
}
