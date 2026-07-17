using System;
using Newtonsoft.Json;

namespace CodexUsageMonitor.Models
{
    public sealed class CursorUsageSummaryResponse
    {
        [JsonProperty("billingCycleStart")]
        public string BillingCycleStart { get; set; }

        [JsonProperty("billingCycleEnd")]
        public string BillingCycleEnd { get; set; }

        [JsonProperty("membershipType")]
        public string MembershipType { get; set; }

        [JsonProperty("limitType")]
        public string LimitType { get; set; }

        [JsonProperty("isUnlimited")]
        public bool IsUnlimited { get; set; }

        [JsonProperty("autoModelSelectedDisplayMessage")]
        public string AutoModelSelectedDisplayMessage { get; set; }

        [JsonProperty("namedModelSelectedDisplayMessage")]
        public string NamedModelSelectedDisplayMessage { get; set; }

        [JsonProperty("individualUsage")]
        public CursorIndividualUsage IndividualUsage { get; set; }
    }

    public sealed class CursorIndividualUsage
    {
        [JsonProperty("plan")]
        public CursorPlanUsage Plan { get; set; }

        [JsonProperty("onDemand")]
        public CursorOnDemandUsage OnDemand { get; set; }
    }

    public sealed class CursorPlanUsage
    {
        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        [JsonProperty("used")]
        public long Used { get; set; }

        [JsonProperty("limit")]
        public long Limit { get; set; }

        [JsonProperty("remaining")]
        public long Remaining { get; set; }

        [JsonProperty("autoPercentUsed")]
        public double AutoPercentUsed { get; set; }

        [JsonProperty("apiPercentUsed")]
        public double ApiPercentUsed { get; set; }

        [JsonProperty("totalPercentUsed")]
        public double TotalPercentUsed { get; set; }
    }

    public sealed class CursorOnDemandUsage
    {
        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        [JsonProperty("used")]
        public long? Used { get; set; }

        [JsonProperty("limit")]
        public long? Limit { get; set; }

        [JsonProperty("remaining")]
        public long? Remaining { get; set; }
    }

    public sealed class CursorUsageSnapshot
    {
        public string MembershipType { get; set; } = "-";
        public string LimitType { get; set; } = "-";
        public bool IsUnlimited { get; set; }
        public double TotalPercentUsed { get; set; }
        public double AutoPercentUsed { get; set; }
        public double ApiPercentUsed { get; set; }
        public bool OnDemandEnabled { get; set; }
        public long? OnDemandUsedCents { get; set; }
        public DateTime? BillingCycleStartUtc { get; set; }
        public DateTime? BillingCycleEndUtc { get; set; }
        public string AutoMessage { get; set; }
        public string ApiMessage { get; set; }
        public DateTime FetchedAtUtc { get; set; }
        public string Error { get; set; }
        public bool Unauthorized { get; set; }

        public bool HasData =>
            string.IsNullOrWhiteSpace(Error) &&
            (BillingCycleEndUtc.HasValue || TotalPercentUsed > 0 || ApiPercentUsed > 0 || AutoPercentUsed > 0);
    }

    public sealed class CursorAuth
    {
        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }
    }

    public sealed class CombinedUsageSnapshot
    {
        public UsageSnapshot Codex { get; set; } = new UsageSnapshot();
        public CursorUsageSnapshot Cursor { get; set; } = new CursorUsageSnapshot();
        public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
