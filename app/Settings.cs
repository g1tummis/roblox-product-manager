using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RobloxProductManager
{
    // Saved in %APPDATA%\RobloxProductManager\app-settings.json.
    // The Client Secret and the Roblox refresh token are encrypted with Windows DPAPI (current user only).
    class Settings
    {
        public const string DefaultScopes =
            "openid profile game-pass:read game-pass:write developer-product:read developer-product:write legacy-universe.badge:manage-and-spend-robux";

        public string ClientId = "", ClientSecret = "", Scopes = DefaultScopes, RefreshToken = "", LastUniverse = "";
        public int Port = 3000;

#if TESTHOOKS
        public static readonly string Folder = Environment.GetEnvironmentVariable("RPM_TEST_SETTINGS");
#else
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxProductManager");
#endif
        static readonly string FilePath = Path.Combine(Folder, "app-settings.json");
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RobloxProductManager.v2");

        public string RedirectUri { get { return "http://localhost:" + Port + "/callback"; } }
        public bool Complete { get { return ClientId != "" && ClientSecret != ""; } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                var d = J.Parse(File.ReadAllText(FilePath));
                s.ClientId = J.Str(d, "clientId");
                s.ClientSecret = Unprotect(J.Str(d, "clientSecret"));
                s.RefreshToken = Unprotect(J.Str(d, "refreshToken"));
                s.LastUniverse = J.Str(d, "lastUniverse");
                if (J.Str(d, "scopes") != "") s.Scopes = J.Str(d, "scopes");
                int port;
                if (int.TryParse(J.Str(d, "port"), out port) && port > 0 && port < 65536) s.Port = port;
            }
            catch (Exception) { /* first run or unreadable file: start fresh */ }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            var d = new Dictionary<string, object>
            {
                { "clientId", ClientId },
                { "clientSecret", Protect(ClientSecret) },
                { "port", Port },
                { "scopes", Scopes },
                { "refreshToken", Protect(RefreshToken) },
                { "lastUniverse", LastUniverse },
            };
            File.WriteAllText(FilePath, J.S.Serialize(d));
        }

        // Settings saved in plain text by the earlier web/console version, used only to pre-fill the setup window.
        public static Settings FromOldVersion()
        {
            try
            {
                var d = J.Parse(File.ReadAllText(Path.Combine(Folder, "settings.json")));
                var s = new Settings { ClientId = J.Str(d, "clientId"), ClientSecret = J.Str(d, "clientSecret") };
                int port;
                if (int.TryParse(J.Str(d, "port"), out port) && port > 0 && port < 65536) s.Port = port;
                return s.ClientId != "" ? s : null;
            }
            catch (Exception) { return null; }
        }

        static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
        }

        static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)); }
            catch (Exception) { return ""; }
        }
    }
}
