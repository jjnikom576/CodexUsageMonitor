using System;
using System.IO;
using CodexUsageMonitor.Models;
using Newtonsoft.Json;

namespace CodexUsageMonitor.Services
{
    public sealed class ClaudeAuthStore
    {
        private readonly string _authPath;

        public ClaudeAuthStore(string authPath = null)
        {
            _authPath = string.IsNullOrWhiteSpace(authPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json")
                : authPath;
        }

        public string AuthPath => _authPath;

        public bool TryLoad(out string accessToken, out string subscriptionType, out string error)
        {
            accessToken = null;
            subscriptionType = null;
            error = null;

            try
            {
                if (!File.Exists(_authPath))
                {
                    error = "Claude credentials not found. Run: claude login";
                    return false;
                }

                var json = File.ReadAllText(_authPath);
                var auth = JsonConvert.DeserializeObject<ClaudeCredentialsFile>(json);
                accessToken = auth?.ClaudeAiOauth?.AccessToken;
                subscriptionType = auth?.ClaudeAiOauth?.SubscriptionType;

                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    error = "Missing accessToken in Claude credentials.json";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
