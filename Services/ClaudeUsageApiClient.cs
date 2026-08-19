using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CodexUsageMonitor.Models;
using Newtonsoft.Json;

namespace CodexUsageMonitor.Services
{
    // Unofficial endpoint used internally by the Claude Code CLI's `/status` command.
    // Not publicly documented; may change without notice.
    public sealed class ClaudeUsageApiClient : IDisposable
    {
        private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private readonly HttpClient _http;

        public ClaudeUsageApiClient()
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CodexUsageMonitor/1.2");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        }

        public async Task<ClaudeUsageSnapshot> FetchAsync(string accessToken, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl))
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
                        if (response.StatusCode == HttpStatusCode.Unauthorized ||
                            response.StatusCode == HttpStatusCode.Forbidden)
                            return ErrorSnapshot("Unauthorized. Open Claude Code to refresh login.", unauthorized: true);

                        var shortBody = body.Length > 180 ? body.Substring(0, 180) + "..." : body;
                        return ErrorSnapshot("HTTP " + (int)response.StatusCode + ": " + shortBody);
                    }

                    ClaudeUsageResponse parsed;
                    try
                    {
                        parsed = JsonConvert.DeserializeObject<ClaudeUsageResponse>(body);
                    }
                    catch (Exception ex)
                    {
                        return ErrorSnapshot("Parse error: " + ex.Message);
                    }

                    return new ClaudeUsageSnapshot
                    {
                        FiveHourUtilization = parsed?.FiveHour?.Utilization ?? -1,
                        FiveHourResetUtc = parsed?.FiveHour?.ResetsAt?.UtcDateTime,
                        SevenDayUtilization = parsed?.SevenDay?.Utilization ?? -1,
                        SevenDayResetUtc = parsed?.SevenDay?.ResetsAt?.UtcDateTime,
                        SevenDaySonnetUtilization = parsed?.SevenDaySonnet?.Utilization ?? -1,
                        SevenDayOpusUtilization = parsed?.SevenDayOpus?.Utilization ?? -1,
                        FetchedAtUtc = DateTime.UtcNow
                    };
                }
            }
        }

        private static ClaudeUsageSnapshot ErrorSnapshot(string message, bool unauthorized = false)
        {
            return new ClaudeUsageSnapshot
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
