using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RobloxProductManager
{
    enum Kind { Pass, Product, Badge }

    class Universe
    {
        public string Id = "", Name = "", OwnerType = "", OwnerName = "";
        public bool IsGroup { get { return OwnerType == "Group"; } }
        public override string ToString()
        {
            return Name + "   (" + (IsGroup ? "Group" + (OwnerName != "" ? ": " + OwnerName : "") : "Yours") + ", ID " + Id + ")";
        }
    }

    class UserInfo
    {
        public string Id = "", Name = "", Username = "";
        public string Display
        {
            get
            {
                if (Name != "" && Username != "" && Name != Username) return Name + " (@" + Username + ")";
                return Username != "" ? Username : (Name != "" ? Name : "User " + Id);
            }
        }
    }

    class Item
    {
        public string Id = "", Name = "", Description = "";
        public long? Price;
        public bool ForSale, Regional, Enabled, Locked;
    }

    class NewItem
    {
        public string Name = "", Description = "", ImagePath = "", Regional = ""; // Regional: "", "true" or "false"
        public long? Price;
        public bool ForSale, Active;
    }

    class ItemChanges
    {
        public string Name, Description, ImagePath;
        public long? Price;
        public bool PriceOrSaleChanged, ForSale;
        public bool? Regional, Enabled;
        public bool Any
        {
            get { return Name != null || Description != null || ImagePath != null || PriceOrSaleChanged || Regional.HasValue || Enabled.HasValue; }
        }
    }

    class ApiException : Exception
    {
        public readonly int Status;
        public readonly bool SignedOut;
        public ApiException(int status, string message, bool signedOut = false) : base(message)
        {
            Status = status;
            SignedOut = signedOut;
        }
    }

    // Small helpers over JavaScriptSerializer's Dictionary<string, object> / object[] output.
    static class J
    {
        public static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static object Parse(string text) { return string.IsNullOrWhiteSpace(text) ? null : S.DeserializeObject(text); }

        public static object G(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(object o, string key)
        {
            var v = G(o, key);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static object[] A(object o, string key) { return G(o, key) as object[] ?? new object[0]; }

        public static bool B(object o, string key) { var v = G(o, key); return v is bool && (bool)v; }

        public static long? L(object o, string key)
        {
            var v = G(o, key);
            if (v == null) return null;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (Exception) { return null; }
        }

        // Turns a Roblox error response into a readable sentence.
        public static string Describe(string text, int status)
        {
            object o = null;
            try { o = Parse(text); } catch (Exception) { }
            if (o is Dictionary<string, object>)
            {
                string m = Str(o, "errorMessage");
                if (m != "") return Str(o, "field") != "" ? m + " (" + Str(o, "field") + ")" : m;
                var errors = A(o, "errors");
                if (errors.Length > 0) return string.Join("; ", errors.Select(e => Str(e, "message") != "" ? Str(e, "message") : Str(e, "code")));
                foreach (var key in new[] { "message", "error_description", "error" })
                    if (Str(o, key) != "") return Str(o, key);
            }
            if (status == 401 || status == 403)
                return "Roblox refused this (" + status + "). You may not have permission to manage this game, or the app is missing a permission scope.";
            string snippet = (text ?? "").Trim();
            if (snippet.Length > 200) snippet = snippet.Substring(0, 200);
            return "Request failed (HTTP " + status + ")" + (snippet != "" ? ": " + snippet : "");
        }
    }

    static class Browser
    {
        public static bool Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
            catch (Exception) { return false; }
        }
    }

    // multipart/form-data body shaped like a browser form post.
    class MultipartForm
    {
        public readonly MultipartFormDataContent Content = new MultipartFormDataContent();

        public void Add(string name, string value)
        {
            var c = new StringContent(value, Encoding.UTF8);
            c.Headers.ContentType = null;
            c.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name) };
            Content.Add(c);
        }

        public void AddFile(string name, string path, byte[] data)
        {
            string fileName = Regex.Replace(Path.GetFileName(path), @"[^\w.\-]", "_");
            var c = new ByteArrayContent(data);
            c.Headers.ContentType = new MediaTypeHeaderValue(MimeFor(path));
            c.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name), FileName = Quote(fileName) };
            Content.Add(c);
        }

        static string Quote(string s) { return "\"" + s.Replace("\"", "") + "\""; }

        static string MimeFor(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".bmp": return "image/bmp";
                default: return "application/octet-stream";
            }
        }
    }

    // Receives the OAuth redirect (http://localhost:PORT/callback) after the user approves in their browser.
    class LoopbackListener : IDisposable
    {
        readonly List<TcpListener> listeners = new List<TcpListener>();
        TaskCompletionSource<NameValueCollection> done;
        string callbackPath = "/callback";

        public LoopbackListener(int port)
        {
            var v4 = new TcpListener(IPAddress.Loopback, port);
            try { v4.Start(); }
            catch (SocketException)
            {
                throw new ApiException(0, "Port " + port + " is already used by another program, so sign-in can't finish. " +
                    "Close that program (for example an older copy of this app), or choose another port in App settings and add the new Redirect URL on Roblox.");
            }
            listeners.Add(v4);
            try { var v6 = new TcpListener(IPAddress.IPv6Loopback, port); v6.Start(); listeners.Add(v6); }
            catch (SocketException) { /* IPv6 loopback not available; IPv4 is enough */ }
        }

        public Task<NameValueCollection> WaitForCallback(string path, CancellationToken ct)
        {
            callbackPath = path;
            done = new TaskCompletionSource<NameValueCollection>();
            ct.Register(() => done.TrySetCanceled());
            foreach (var l in listeners) AcceptLoop(l);
            return done.Task;
        }

        async void AcceptLoop(TcpListener listener)
        {
            while (!done.Task.IsCompleted)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; } // listener stopped
                Handle(client);
            }
        }

        async void Handle(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var read = ReadHead(stream);
                    if (await Task.WhenAny(read, Task.Delay(5000)) != read) return; // e.g. a browser pre-connect that never sends
                    string head = read.Result;
                    int sp1 = head.IndexOf(' '), sp2 = sp1 < 0 ? -1 : head.IndexOf(' ', sp1 + 1);
                    if (sp1 < 0 || sp2 < 0) return;
                    string target = head.Substring(sp1 + 1, sp2 - sp1 - 1);
                    int qi = target.IndexOf('?');
                    string path = qi < 0 ? target : target.Substring(0, qi);
                    var query = ParseQuery(qi < 0 ? "" : target.Substring(qi + 1));

                    if (head.StartsWith("GET ") && path == callbackPath && (query["code"] != null || query["error"] != null))
                    {
                        bool ok = query["error"] == null;
                        await Respond(stream, 200, Page(ok ? "Roblox sent you back to the app" : "Sign-in did not finish",
                            ok ? "You can close this tab and return to Roblox Product Manager." : "You can close this tab. Go back to Roblox Product Manager to try again."));
                        done.TrySetResult(query);
                    }
                    else
                    {
                        await Respond(stream, 404, Page("Not found", "This address is only used to finish signing in to Roblox Product Manager."));
                    }
                }
            }
            catch (Exception) { /* a broken connection must never crash the app */ }
        }

        static async Task<string> ReadHead(NetworkStream stream)
        {
            var buf = new byte[16384];
            int total = 0;
            while (total < buf.Length)
            {
                int n = await stream.ReadAsync(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
                if (Encoding.ASCII.GetString(buf, 0, total).Contains("\r\n\r\n")) break;
            }
            return Encoding.ASCII.GetString(buf, 0, total);
        }

        static async Task Respond(NetworkStream stream, int status, string html)
        {
            byte[] body = Encoding.UTF8.GetBytes(html);
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") +
                "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, 0, head.Length);
            await stream.WriteAsync(body, 0, body.Length);
            await stream.FlushAsync();
        }

        static string Page(string title, string text)
        {
            return "<!doctype html><html><head><meta charset=\"utf-8\"><title>Roblox Product Manager</title></head>" +
                "<body style=\"font:16px system-ui,sans-serif;margin:48px;color:#111\"><h2>" + title + "</h2><p>" + text + "</p></body></html>";
        }

        static NameValueCollection ParseQuery(string q)
        {
            var c = new NameValueCollection();
            foreach (var part in q.Split('&'))
            {
                if (part == "") continue;
                int i = part.IndexOf('=');
                string k = i < 0 ? part : part.Substring(0, i), v = i < 0 ? "" : part.Substring(i + 1);
                c[Unescape(k)] = Unescape(v);
            }
            return c;
        }

        static string Unescape(string s) { return Uri.UnescapeDataString(s.Replace('+', ' ')); }

        public void Dispose()
        {
            foreach (var l in listeners) { try { l.Stop(); } catch (Exception) { } }
        }
    }

    // Roblox OAuth 2.0 (authorization code + PKCE + state + nonce) and the Open Cloud calls this app uses.
    class RobloxClient
    {
#if TESTHOOKS
        // Test builds only (build with /define:TESTHOOKS): talk to a local fake Roblox server instead.
        static readonly string TestRoot = Environment.GetEnvironmentVariable("RPM_TEST_ROOT");
        static readonly string OAuth = TestRoot + "/oauth/v1", Apis = TestRoot, BadgesApi = TestRoot + "/badges", GamesApi = TestRoot + "/games";
#else
        const string OAuth = "https://apis.roblox.com/oauth/v1";
        const string Apis = "https://apis.roblox.com";
        const string BadgesApi = "https://badges.roblox.com";
        const string GamesApi = "https://games.roblox.com";
#endif
        readonly Settings settings;
        readonly HttpClient http;
        readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);
        string accessToken;
        DateTime accessExpiresUtc = DateTime.MinValue;
        public UserInfo User;

        public RobloxClient(Settings settings)
        {
            this.settings = settings;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxProductManager/2.0");
        }

        // ---------- sign-in ----------
        public async Task SignIn(bool chooseGames, CancellationToken ct)
        {
            string verifier = RandomUrlSafe(48), state = RandomUrlSafe(24), nonce = RandomUrlSafe(16);
            string url = OAuth + "/authorize?client_id=" + E(settings.ClientId) +
                "&redirect_uri=" + E(settings.RedirectUri) +
                "&scope=" + E(settings.Scopes) +
                "&response_type=code&state=" + state + "&nonce=" + nonce +
                "&code_challenge=" + Sha256Url(verifier) + "&code_challenge_method=S256" +
                (chooseGames ? "&prompt=consent" : "");

            NameValueCollection q;
            using (var listener = new LoopbackListener(settings.Port))
            {
#if TESTHOOKS
                var wait = listener.WaitForCallback("/callback", ct);
                var fakeBrowser = Task.Run(() => new HttpClient().GetAsync(url)); // the fake server redirects straight to the callback
                q = await wait;
#else
                if (!Browser.Open(url)) throw new ApiException(0, "Could not open your web browser to sign in.");
                q = await listener.WaitForCallback("/callback", ct);
#endif
            }
            if (q["state"] != state) throw new ApiException(0, "Sign-in did not match this window. Please try again.");
            if (!string.IsNullOrEmpty(q["error"]))
                throw new ApiException(0, q["error"] == "access_denied" ? "Sign-in was cancelled on Roblox." : "Roblox sign-in failed: " + (q["error_description"] ?? q["error"]));

            var tokens = await OAuthPost("/token", new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" }, { "code", q["code"] ?? "" }, { "code_verifier", verifier },
            });
            var claims = DecodeJwt(J.Str(tokens, "id_token"));
            if (J.Str(claims, "nonce") != nonce) throw new ApiException(0, "Sign-in check failed. Please try again.");
            SetTokens(tokens);
            User = new UserInfo { Id = J.Str(claims, "sub"), Name = J.Str(claims, "name"), Username = J.Str(claims, "preferred_username") };
        }

        // Uses the saved refresh token so the user stays signed in between launches.
        public async Task<bool> TryRestore()
        {
            if (settings.RefreshToken == "") return false;
            try { await RefreshNow(); }
            catch (ApiException ex) { if (ex.SignedOut) return false; throw; }
            var info = await Send(HttpMethod.Get, OAuth + "/userinfo", null, true);
            User = new UserInfo { Id = J.Str(info, "sub"), Name = J.Str(info, "name"), Username = J.Str(info, "preferred_username") };
            return true;
        }

        public async Task SignOut()
        {
            string refresh = settings.RefreshToken;
            Forget();
            if (refresh == "") return;
            try { await OAuthPost("/token/revoke", new Dictionary<string, string> { { "token", refresh } }); }
            catch (ApiException) { /* best effort */ }
        }

        public void Forget()
        {
            accessToken = null;
            accessExpiresUtc = DateTime.MinValue;
            User = null;
            settings.RefreshToken = "";
            TrySave();
        }

        void SetTokens(object t)
        {
            accessToken = J.Str(t, "access_token");
            long seconds = J.L(t, "expires_in") ?? 900;
            accessExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60));
            string refresh = J.Str(t, "refresh_token");
            if (refresh != "") settings.RefreshToken = refresh; // refresh tokens are single-use, so always keep the newest
            TrySave();
        }

        void TrySave() { try { settings.Save(); } catch (Exception) { } }

        async Task<string> GetAccessToken(bool force)
        {
            if (!force && accessToken != null && DateTime.UtcNow < accessExpiresUtc) return accessToken;
            string before = accessToken;
            await refreshLock.WaitAsync();
            try
            {
                if (accessToken == null || DateTime.UtcNow >= accessExpiresUtc || (force && accessToken == before)) await RefreshNow();
                return accessToken;
            }
            finally { refreshLock.Release(); }
        }

        async Task RefreshNow()
        {
            if (settings.RefreshToken == "") throw SignedOutError();
            object t;
            try
            {
                t = await OAuthPost("/token", new Dictionary<string, string> { { "grant_type", "refresh_token" }, { "refresh_token", settings.RefreshToken } });
            }
            catch (ApiException ex)
            {
                if (ex.Status == 400 || ex.Status == 401) { Forget(); throw SignedOutError(); }
                throw;
            }
            SetTokens(t);
        }

        static ApiException SignedOutError() { return new ApiException(401, "Your Roblox sign-in expired. Please sign in again.", true); }
        static ApiException Offline() { return new ApiException(0, "Could not reach Roblox. Check your internet connection and try again."); }
        static ApiException TooSlow() { return new ApiException(0, "Roblox took too long to respond. Try again."); }

        async Task<object> OAuthPost(string path, Dictionary<string, string> form)
        {
            form["client_id"] = settings.ClientId;
            form["client_secret"] = settings.ClientSecret;
            HttpResponseMessage resp;
            try { resp = await http.PostAsync(OAuth + path, new FormUrlEncodedContent(form)); }
            catch (HttpRequestException) { throw Offline(); }
            catch (TaskCanceledException) { throw TooSlow(); }
            using (resp)
            {
                int status = (int)resp.StatusCode;
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) throw new ApiException(status, "Roblox sign-in error (" + status + "): " + J.Describe(text, status));
                return J.Parse(text) ?? new Dictionary<string, object>();
            }
        }

        async Task<object> Send(HttpMethod method, string url, Func<HttpContent> body, bool auth)
        {
            bool forceRefresh = false;
            for (int attempt = 1; ; attempt++)
            {
                using (var req = new HttpRequestMessage(method, url))
                {
                    if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessToken(forceRefresh));
                    if (body != null) req.Content = body(); // rebuilt on every attempt
                    HttpResponseMessage resp;
                    try { resp = await http.SendAsync(req); }
                    catch (HttpRequestException) { throw Offline(); }
                    catch (TaskCanceledException) { throw TooSlow(); }
                    using (resp)
                    {
                        int status = (int)resp.StatusCode;
                        if (status == 429 && attempt < 3) { await Task.Delay(RetryDelay(resp, attempt)); continue; }
                        if (status == 401 && auth && attempt == 1) { forceRefresh = true; continue; }
                        string text = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode) throw new ApiException(status, J.Describe(text, status));
                        try { return J.Parse(text); } catch (ArgumentException) { return text; }
                    }
                }
            }
        }

        static TimeSpan RetryDelay(HttpResponseMessage resp, int attempt)
        {
            var ra = resp.Headers.RetryAfter;
            if (ra != null && ra.Delta.HasValue) return TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, ra.Delta.Value.TotalSeconds)));
            return TimeSpan.FromSeconds(2 * attempt);
        }

        // ---------- games ----------
        public async Task<List<Universe>> LoadUniverses()
        {
            var info = await OAuthPost("/token/resources", new Dictionary<string, string> { { "token", await GetAccessToken(false) } });
            var ownerOf = new Dictionary<string, string>();
            var ids = new List<string>();
            foreach (var ri in J.A(info, "resource_infos"))
            {
                string ownerType = J.Str(J.G(ri, "owner"), "type");
                foreach (var id in J.A(J.G(J.G(ri, "resources"), "universe"), "ids"))
                {
                    string s = Convert.ToString(id, CultureInfo.InvariantCulture);
                    if (!ownerOf.ContainsKey(s)) { ownerOf[s] = ownerType; ids.Add(s); }
                }
            }
            var details = new Dictionary<string, object>();
            for (int i = 0; i < ids.Count; i += 50)
            {
                try
                {
                    var j = await Send(HttpMethod.Get, GamesApi + "/v1/games?universeIds=" + string.Join(",", ids.Skip(i).Take(50)), null, false);
                    foreach (var g in J.A(j, "data")) details[J.Str(g, "id")] = g;
                }
                catch (ApiException) { /* names are cosmetic; fall back to IDs */ }
            }
            return ids.Select(id =>
            {
                object g;
                details.TryGetValue(id, out g);
                string name = J.Str(g, "name");
                return new Universe
                {
                    Id = id,
                    Name = name != "" ? name : "Experience " + id,
                    OwnerType = ownerOf[id] != "" ? ownerOf[id] : J.Str(J.G(g, "creator"), "type"),
                    OwnerName = J.Str(J.G(g, "creator"), "name"),
                };
            }).OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ---------- items ----------
        public async Task<List<Item>> ListItems(Kind kind, string universe)
        {
            var list = new List<Item>();
            string token = "";
            for (int page = 0; page < 200; page++)
            {
                if (kind == Kind.Badge)
                {
                    var d = await Send(HttpMethod.Get, BadgesApi + "/v1/universes/" + universe + "/badges?limit=100&sortOrder=Asc" +
                        (token != "" ? "&cursor=" + E(token) : ""), null, false);
                    foreach (var b in J.A(d, "data"))
                        list.Add(new Item { Id = J.Str(b, "id"), Name = J.Str(b, "name"), Description = J.Str(b, "description"), Enabled = J.B(b, "enabled") });
                    token = J.Str(d, "nextPageCursor");
                }
                else
                {
                    bool pass = kind == Kind.Pass;
                    string url = pass
                        ? Apis + "/game-passes/v1/universes/" + universe + "/game-passes/creator?pageSize=50"
                        : Apis + "/developer-products/v2/universes/" + universe + "/developer-products/creator?pageSize=50";
                    if (token != "") url += "&pageToken=" + E(token);
                    var d = await Send(HttpMethod.Get, url, null, true);
                    foreach (var x in J.A(d, pass ? "gamePasses" : "developerProducts"))
                    {
                        var pi = J.G(x, "priceInformation");
                        list.Add(new Item
                        {
                            Id = J.Str(x, pass ? "gamePassId" : "productId"),
                            Name = J.Str(x, "name"),
                            Description = J.Str(x, "description"),
                            Price = J.L(pi, "defaultPriceInRobux"),
                            ForSale = J.B(x, "isForSale"),
                            Regional = J.A(pi, "enabledFeatures").Any(f => Convert.ToString(f) == "RegionalPricing"),
                            Locked = J.B(x, "isImmutable"),
                        });
                    }
                    token = J.Str(d, "nextPageToken");
                }
                if (token == "") break;
            }
            return list;
        }

        public async Task<string> CreateItem(Kind kind, Universe u, NewItem n, int expectedBadgeCost)
        {
            string url = kind == Kind.Pass ? Apis + "/game-passes/v1/universes/" + u.Id + "/game-passes"
                : kind == Kind.Product ? Apis + "/developer-products/v2/universes/" + u.Id + "/developer-products"
                : Apis + "/legacy-badges/v1/universes/" + u.Id + "/badges";
            byte[] image = n.ImagePath != "" ? File.ReadAllBytes(n.ImagePath) : null;
            var r = await Send(HttpMethod.Post, url, () =>
            {
                var f = new MultipartForm();
                f.Add("name", n.Name);
                if (n.Description != "") f.Add("description", n.Description);
                if (kind == Kind.Badge)
                {
                    f.Add("paymentSourceType", u.IsGroup ? "2" : "1");
                    f.Add("expectedCost", expectedBadgeCost.ToString(CultureInfo.InvariantCulture)); // Roblox refuses if the real cost differs
                    f.Add("isActive", Bool(n.Active));
                }
                else
                {
                    f.Add("isForSale", Bool(n.ForSale));
                    if (n.Price.HasValue) f.Add("price", n.Price.Value.ToString(CultureInfo.InvariantCulture));
                    if (n.Regional != "") f.Add("isRegionalPricingEnabled", n.Regional);
                }
                if (image != null) f.AddFile(kind == Kind.Badge ? "files" : "imageFile", n.ImagePath, image);
                return f.Content;
            }, true);
            return J.Str(r, kind == Kind.Pass ? "gamePassId" : kind == Kind.Product ? "productId" : "id");
        }

        public async Task UpdateItem(Kind kind, string universe, string id, ItemChanges ch)
        {
            if (kind == Kind.Badge)
            {
                var d = new Dictionary<string, object>();
                if (ch.Name != null) d["name"] = ch.Name;
                if (ch.Description != null) d["description"] = ch.Description;
                if (ch.Enabled.HasValue) d["enabled"] = ch.Enabled.Value;
                string json = J.S.Serialize(d);
                await Send(new HttpMethod("PATCH"), Apis + "/legacy-badges/v1/badges/" + id, () => new StringContent(json, Encoding.UTF8, "application/json"), true);
                return;
            }
            string url = kind == Kind.Pass
                ? Apis + "/game-passes/v1/universes/" + universe + "/game-passes/" + id
                : Apis + "/developer-products/v2/universes/" + universe + "/developer-products/" + id;
            byte[] image = ch.ImagePath != null ? File.ReadAllBytes(ch.ImagePath) : null;
            await Send(new HttpMethod("PATCH"), url, () =>
            {
                var f = new MultipartForm();
                if (ch.Name != null) f.Add("name", ch.Name);
                if (ch.Description != null) f.Add("description", ch.Description);
                if (ch.PriceOrSaleChanged)
                {
                    f.Add("isForSale", Bool(ch.ForSale));
                    if (ch.Price.HasValue) f.Add("price", ch.Price.Value.ToString(CultureInfo.InvariantCulture));
                }
                if (ch.Regional.HasValue) f.Add("isRegionalPricingEnabled", Bool(ch.Regional.Value));
                if (image != null) f.AddFile("imageFile", ch.ImagePath, image);
                return f.Content;
            }, true);
        }

        public async Task<int> FreeBadgeQuota(string universe)
        {
            var r = await Send(HttpMethod.Get, BadgesApi + "/v1/universes/" + universe + "/free-badges-quota", null, false);
            int n;
            if (r == null || !int.TryParse(Convert.ToString(r, CultureInfo.InvariantCulture), out n) || n < 0)
                throw new ApiException(0, "Could not read the free badge count from Roblox.");
            return n;
        }

        // ---------- helpers ----------
        static string Bool(bool b) { return b ? "true" : "false"; }
        static string E(string s) { return Uri.EscapeDataString(s); }

        static string RandomUrlSafe(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return B64Url(b);
        }

        static string Sha256Url(string s)
        {
            using (var sha = SHA256.Create()) return B64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(s)));
        }

        static string B64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        static object DecodeJwt(string jwt)
        {
            try
            {
                string p = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
                p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
                return J.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
            }
            catch (Exception) { return null; }
        }
    }
}
