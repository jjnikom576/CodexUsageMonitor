using System;
using System.IO;
using CodexUsageMonitor.Models;
using Newtonsoft.Json;

namespace CodexUsageMonitor.Services
{
    public sealed class AuthStore
    {
        private readonly string _authPath;

        public AuthStore(string authPath = null)
        {
            _authPath = string.IsNullOrWhiteSpace(authPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json")
                : authPath;
        }

        public string AuthPath => _authPath;

        public bool TryLoad(out string accessToken, out string accountId, out string error)
        {
            accessToken = null;
            accountId = null;
            error = null;

            try
            {
                if (!File.Exists(_authPath))
                {
                    error = "auth.json not found. Run: codex login";
                    return false;
                }

                var json = File.ReadAllText(_authPath);
                var auth = JsonConvert.DeserializeObject<CodexAuth>(json);
                accessToken = auth?.Tokens?.AccessToken;
                accountId = auth?.Tokens?.AccountId;

                if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(accountId))
                {
                    error = "Missing access_token or account_id in auth.json";
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
