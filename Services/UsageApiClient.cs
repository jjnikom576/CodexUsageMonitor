using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CodexUsageMonitor.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
                JObject raw;
                try
                {
                    parsed = JsonConvert.DeserializeObject<UsageResponse>(body);
                    raw = JObject.Parse(body);
                }
                catch (Exception ex)
                {
                    return ErrorSnapshot("Parse error: " + ex.Message);
                }

                var rateLimit = parsed?.RateLimit;
                return new UsageSnapshot
                {
                    PlanType = parsed?.PlanType ?? "-",
                    Email = parsed?.Email ?? "-",
                    Allowed = rateLimit?.Allowed ?? true,
                    LimitReached = rateLimit?.LimitReached ?? false,
                    Primary = rateLimit?.PrimaryWindow,
                    Secondary = rateLimit?.SecondaryWindow,
                    Windows = BuildUsageWindows(rateLimit, raw["rate_limit"]),
                    FetchedAtUtc = DateTime.UtcNow
                };
            }
        }

        private static List<UsageLimitWindow> BuildUsageWindows(RateLimitBlock rateLimit, JToken rateLimitToken)
        {
            var windows = new List<UsageLimitWindow>();

            if (rateLimit != null)
            {
                AddWindow(windows, "primary_window", rateLimit.PrimaryWindow);
                AddWindow(windows, "secondary_window", rateLimit.SecondaryWindow);
            }

            CollectWindowTokens(windows, rateLimitToken, "rate_limit");

            if (rateLimit?.ExtraFields != null)
            {
                foreach (var field in rateLimit.ExtraFields)
                {
                    var token = field.Value;
                    if (token == null || token.Type != JTokenType.Object)
                        continue;

                    UsageWindow window;
                    try
                    {
                        window = token.ToObject<UsageWindow>();
                    }
                    catch
                    {
                        continue;
                    }

                    AddWindow(windows, field.Key, window);
                }
            }

            windows.Sort((left, right) =>
            {
                var usedCompare = right.Window.UsedPercent.CompareTo(left.Window.UsedPercent);
                if (usedCompare != 0)
                    return usedCompare;

                return left.Window.LimitWindowSeconds.CompareTo(right.Window.LimitWindowSeconds);
            });
            return windows;
        }

        private static void CollectWindowTokens(List<UsageLimitWindow> windows, JToken token, string key)
        {
            if (token == null)
                return;

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                if (obj["limit_window_seconds"] != null)
                {
                    UsageWindow window;
                    try
                    {
                        window = obj.ToObject<UsageWindow>();
                    }
                    catch
                    {
                        window = null;
                    }

                    AddWindow(windows, key, window);
                }

                foreach (var property in obj.Properties())
                    CollectWindowTokens(windows, property.Value, property.Name);
            }
            else if (token.Type == JTokenType.Array)
            {
                var index = 0;
                foreach (var child in token.Children())
                {
                    CollectWindowTokens(windows, child, key + "[" + index + "]");
                    index++;
                }
            }
        }

        private static void AddWindow(List<UsageLimitWindow> windows, string key, UsageWindow window)
        {
            if (!IsUsableWindow(window))
                return;

            foreach (var existing in windows)
            {
                if (existing.Window.LimitWindowSeconds == window.LimitWindowSeconds)
                    return;
            }

            windows.Add(new UsageLimitWindow
            {
                Key = key,
                Window = window
            });
        }

        private static bool IsUsableWindow(UsageWindow window)
        {
            return window != null && window.LimitWindowSeconds > 0;
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
