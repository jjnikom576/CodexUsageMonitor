using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CodexUsageMonitor.Models;
using Newtonsoft.Json;

namespace CodexUsageMonitor.Services
{
    public sealed class UsageApiClient : IDisposable
    {
        private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
        private readonly HttpClient _http;

        public UsageApiClient()
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CodexUsageMonitor/1.0");
        }

        public async Task<UsageSnapshot> FetchAsync(string accessToken, string accountId, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return ErrorSnapshot("Network: " + ex.Message);
                }

                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        return ErrorSnapshot("Unauthorized. Run: codex login");

                    var shortBody = body.Length > 180 ? body.Substring(0, 180) + "..." : body;
                    return ErrorSnapshot("HTTP " + (int)response.StatusCode + ": " + shortBody);
                }

                UsageResponse parsed;
                try
                {
                    parsed = JsonConvert.DeserializeObject<UsageResponse>(body);
                }
                catch (Exception ex)
                {
                    return ErrorSnapshot("Parse error: " + ex.Message);
                }

                return new UsageSnapshot
                {
                    PlanType = parsed?.PlanType ?? "-",
                    Email = parsed?.Email ?? "-",
                    Allowed = parsed?.RateLimit?.Allowed ?? true,
                    LimitReached = parsed?.RateLimit?.LimitReached ?? false,
                    Primary = parsed?.RateLimit?.PrimaryWindow,
                    Secondary = parsed?.RateLimit?.SecondaryWindow,
                    FetchedAtUtc = DateTime.UtcNow
                };
            }
        }

        private static UsageSnapshot ErrorSnapshot(string message)
        {
            return new UsageSnapshot
            {
                Error = message,
                FetchedAtUtc = DateTime.UtcNow
            };
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
