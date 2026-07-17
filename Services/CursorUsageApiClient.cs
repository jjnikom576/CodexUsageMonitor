using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CodexUsageMonitor.Models;
using Newtonsoft.Json;

namespace CodexUsageMonitor.Services
{
    public sealed class CursorUsageApiClient : IDisposable
    {
        private const string UsageSummaryUrl = "https://api2.cursor.sh/auth/usage-summary";
        private readonly HttpClient _http;

        public CursorUsageApiClient()
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CodexUsageMonitor/1.1");
        }

        public async Task<CursorUsageSnapshot> FetchAsync(string accessToken, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, UsageSummaryUrl))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return ErrorSnapshot("Network: " + ex.Message);
                }

                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                            return ErrorSnapshot("Unauthorized. Log in to Cursor IDE.", unauthorized: true);

                        var shortBody = body.Length > 180 ? body.Substring(0, 180) + "..." : body;
                        return ErrorSnapshot("HTTP " + (int)response.StatusCode + ": " + shortBody);
                    }

                    CursorUsageSummaryResponse parsed;
                    try
                    {
                        parsed = JsonConvert.DeserializeObject<CursorUsageSummaryResponse>(body);
                    }
                    catch (Exception ex)
                    {
                        return ErrorSnapshot("Parse error: " + ex.Message);
                    }

                    var plan = parsed?.IndividualUsage?.Plan;
                    var onDemand = parsed?.IndividualUsage?.OnDemand;

                    return new CursorUsageSnapshot
                    {
                        MembershipType = parsed?.MembershipType ?? "-",
                        LimitType = parsed?.LimitType ?? "-",
                        IsUnlimited = parsed?.IsUnlimited ?? false,
                        TotalPercentUsed = plan?.TotalPercentUsed ?? 0,
                        AutoPercentUsed = plan?.AutoPercentUsed ?? 0,
                        ApiPercentUsed = plan?.ApiPercentUsed ?? 0,
                        OnDemandEnabled = onDemand?.Enabled ?? false,
                        OnDemandUsedCents = onDemand?.Used,
                        BillingCycleStartUtc = ParseUtc(parsed?.BillingCycleStart),
                        BillingCycleEndUtc = ParseUtc(parsed?.BillingCycleEnd),
                        AutoMessage = parsed?.AutoModelSelectedDisplayMessage,
                        ApiMessage = parsed?.NamedModelSelectedDisplayMessage,
                        FetchedAtUtc = DateTime.UtcNow
                    };
                }
            }
        }

        private static DateTime? ParseUtc(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed.ToUniversalTime();

            return null;
        }

        private static CursorUsageSnapshot ErrorSnapshot(string message, bool unauthorized = false)
        {
            return new CursorUsageSnapshot
            {
                Error = message,
                Unauthorized = unauthorized,
                FetchedAtUtc = DateTime.UtcNow
            };
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
