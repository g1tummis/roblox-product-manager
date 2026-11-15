using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RobloxProductManager
{
    // Saved in %APPDATA%\RobloxProductManager\app-settings.json.
    // The optional Client Secret and the Roblox refresh token are encrypted with Windows DPAPI (current user only).
    class Settings
    {
        // The app's own Roblox OAuth app. A Client ID is public (it appears in every sign-in link), so it is safe to ship.
        // No Client Secret ships with the app: it signs in as a public client using PKCE.
        public const string BuiltInClientId = "7671318064412716810";
        static readonly string[] PreviousBuiltInClientIds = { "3030139641816668332" };

        public const string DefaultScopes =
            "openid profile game-pass:read game-pass:write developer-product:read developer-product:write legacy-universe.badge:manage-and-spend-robux";

        const string FileVersion = "2.1";

        public string ClientId = BuiltInClientId, ClientSecret = "", Scopes = DefaultScopes, RefreshToken = "", LastUniverse = "";
        public int Port = 3000;

        // Badge IDs seen per game. Roblox's public badge list hides disabled badges,
        // so the app remembers IDs and looks those badges up one by one.
        readonly Dictionary<string, HashSet<string>> knownBadges = new Dictionary<string, HashSet<string>>();

        public HashSet<string> KnownBadges(string universe)
        {
            HashSet<string> set;
            if (!knownBadges.TryGetValue(universe, out set)) knownBadges[universe] = set = new HashSet<string>();
            return set;
        }

#if TESTHOOKS
        public static readonly string Folder = Environment.GetEnvironmentVariable("RPM_TEST_SETTINGS");
#else
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxProductManager");
#endif
        static readonly string FilePath = Path.Combine(Folder, "app-settings.json");

        // Icon cache: %LOCALAPPDATA%\RobloxProductManager\icons\<passes|products|badges>\<item id>.png
#if TESTHOOKS
        public static readonly string IconFolder = Path.Combine(Folder, "icons");
#else
        public static readonly string IconFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxProductManager", "icons");
#endif
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RobloxProductManager.v2");

        public string RedirectUri { get { return "http://localhost:" + Port + "/callback"; } }
        public bool Complete { get { return ClientId != ""; } }
        public bool UsingBuiltInApp { get { return ClientId == BuiltInClientId; } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                var d = J.Parse(File.ReadAllText(FilePath));
                if (J.Str(d, "clientId") != "") s.ClientId = J.Str(d, "clientId");
                s.ClientSecret = Unprotect(J.Str(d, "clientSecret"));
                s.RefreshToken = Unprotect(J.Str(d, "refreshToken"));
                s.LastUniverse = J.Str(d, "lastUniverse");
                if (J.Str(d, "scopes") != "") s.Scopes = J.Str(d, "scopes");
                int port;
                if (int.TryParse(J.Str(d, "port"), out port) && port > 0 && port < 65536) s.Port = port;
                var kb = J.G(d, "knownBadges") as Dictionary<string, object>;
                if (kb != null)
                    foreach (var pair in kb)
                        foreach (var id in (pair.Value as object[]) ?? new object[0])
                            s.KnownBadges(pair.Key).Add(Convert.ToString(id));

                // v2.0.0 asked every user for the built-in app's secret. That secret is being replaced,
                // so forget any copy saved by v2.0.0 and sign in without it.
                // Earlier builds used a different built-in app; move those settings to the current one.
                if (Array.IndexOf(PreviousBuiltInClientIds, s.ClientId) >= 0)
                {
                    s.ClientId = BuiltInClientId;
                    s.ClientSecret = "";
                    s.RefreshToken = "";
                    s.LastUniverse = "";
                    s.Save();
                }
                else if (J.Str(d, "version") == "" && s.UsingBuiltInApp && s.ClientSecret != "")
                {
                    s.ClientSecret = "";
                    s.RefreshToken = "";
                    s.Save();
                }
            }
            catch (Exception) { /* first run or unreadable file: start fresh */ }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            var d = new Dictionary<string, object>
            {
                { "version", FileVersion },
                { "clientId", ClientId },
                { "clientSecret", Protect(ClientSecret) },
                { "port", Port },
                { "scopes", Scopes },
                { "refreshToken", Protect(RefreshToken) },
                { "lastUniverse", LastUniverse },
                { "knownBadges", knownBadges.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value.ToArray()) },
            };
            File.WriteAllText(FilePath, J.S.Serialize(d));
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
