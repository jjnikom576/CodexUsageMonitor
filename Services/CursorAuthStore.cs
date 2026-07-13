using System;
using System.IO;
using CodexUsageMonitor.Models;
using Newtonsoft.Json;

namespace CodexUsageMonitor.Services
{
    public sealed class CursorAuthStore
    {
        private readonly string _authPath;

        public CursorAuthStore(string authPath = null)
        {
            _authPath = string.IsNullOrWhiteSpace(authPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "auth.json")
                : authPath;
        }

        public string AuthPath => _authPath;

        public bool TryLoad(out string accessToken, out string error)
        {
            accessToken = null;
            error = null;

            try
            {
                if (!File.Exists(_authPath))
                {
                    error = "Cursor auth.json not found. Log in to Cursor IDE.";
                    return false;
                }

                var json = File.ReadAllText(_authPath);
                var auth = JsonConvert.DeserializeObject<CursorAuth>(json);
                accessToken = auth?.AccessToken;

                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    error = "Missing accessToken in Cursor auth.json";
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
