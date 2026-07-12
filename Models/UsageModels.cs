using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexUsageMonitor.Models
{
    public sealed class UsageResponse
    {
        [JsonProperty("plan_type")]
        public string PlanType { get; set; }

        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("rate_limit")]
        public RateLimitBlock RateLimit { get; set; }
    }

    public sealed class RateLimitBlock
    {
        [JsonProperty("allowed")]
        public bool Allowed { get; set; }

        [JsonProperty("limit_reached")]
        public bool LimitReached { get; set; }

        [JsonProperty("primary_window")]
        public UsageWindow PrimaryWindow { get; set; }

        [JsonProperty("secondary_window")]
        public UsageWindow SecondaryWindow { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtraFields { get; set; }
    }

    public sealed class UsageWindow
    {
        [JsonProperty("used_percent")]
        public double UsedPercent { get; set; }

        [JsonProperty("limit_window_seconds")]
        public long LimitWindowSeconds { get; set; }

        [JsonProperty("reset_after_seconds")]
        public long ResetAfterSeconds { get; set; }

        [JsonProperty("reset_at")]
        public long ResetAt { get; set; }
    }

    public sealed class UsageSnapshot
    {
        public string PlanType { get; set; } = "-";
        public string Email { get; set; } = "-";
        public bool Allowed { get; set; }
        public bool LimitReached { get; set; }
        public UsageWindow Primary { get; set; }
        public UsageWindow Secondary { get; set; }
        public List<UsageLimitWindow> Windows { get; set; } = new List<UsageLimitWindow>();
        public DateTime FetchedAtUtc { get; set; }
        public string Error { get; set; }

        public bool HasData => Windows.Count > 0 || Primary != null || Secondary != null;
    }

    public sealed class UsageLimitWindow
    {
        public string Key { get; set; }
        public UsageWindow Window { get; set; }
    }

    public sealed class CodexAuth
    {
        [JsonProperty("tokens")]
        public CodexTokens Tokens { get; set; }
    }

    public sealed class CodexTokens
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("account_id")]
        public string AccountId { get; set; }
    }
}
