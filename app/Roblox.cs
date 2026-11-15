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
            return Name + " (" + (IsGroup ? "Group" + (OwnerName != "" ? ": " + OwnerName : "") : "Yours") + ", ID " + Id + ")";
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
        public bool ForSale, Regional, Managed, Enabled, Locked;
        public string IconAssetId = ""; // developer products: the icon image asset (used to fetch its thumbnail)
    }

    class NewItem
    {
        public string Name = "", Description = "", ImagePath = "", Regional = "", Managed = ""; // Regional / Managed: "", "true" or "false"
        public long? Price;
        public bool ForSale, Active;
    }

    // A Marketplace avatar item (classic clothing or accessory) read with an API key.
    class MarketItem
    {
        public string Id = "", Name = "", Description = "", CollectibleItemId = "", Moderation = "";
        public int AssetType;
        public long? Price;
        public bool OnSale, Free; public bool Resellable;
    }

    class ItemChanges
    {
        public string Name, Description, ImagePath;
        public long? Price;
        public bool PriceOrSaleChanged, ForSale;
        public bool? Managed, Enabled;
        public bool Any
        {
            get { return Name != null || Description != null || ImagePath != null || PriceOrSaleChanged || Managed.HasValue || Enabled.HasValue; }
        }
    }

    class ApiException : Exception
    {
        public readonly int Status;
        public readonly bool SignedOut;
        public bool Uncertain; // a create/update request whose result is unknown (timeout or dropped connection)
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
        // Handles every error shape Roblox uses: Open Cloud {errorMessage, field, hint}, newer {code, message},
        // legacy web APIs {errors:[{code, message, userFacingMessage}]}, OAuth {error, error_description},
        // and non-JSON bodies such as HTML outage pages.
        public static string Describe(string text, int status)
        {
            object o = null;
            try { o = Parse(text); } catch (Exception) { }
            string m = "";
            if (o is Dictionary<string, object>)
            {
                m = Str(o, "errorMessage");
                if (m != "")
                {
                    if (Str(o, "field") != "" && m.IndexOf(Str(o, "field"), StringComparison.OrdinalIgnoreCase) < 0) m += " (" + Str(o, "field") + ")";
                    if (Str(o, "hint") != "") m += (m.EndsWith(".") || m.EndsWith("!") || m.EndsWith(")") ? " " : ". ") + Str(o, "hint");
                }
                var errors = A(o, "errors");
                if (m == "" && errors.Length > 0)
                    m = string.Join("; ", errors.Select(e =>
                        Str(e, "userFacingMessage") != "" && Str(e, "userFacingMessage") != "Something went wrong" ? Str(e, "userFacingMessage")
                        : Str(e, "message") != "" ? Str(e, "message") : "error code " + Str(e, "code")));
                if (m == "")
                    foreach (var key in new[] { "message", "error_description", "error", "title" })
                        if (Str(o, key) != "") { m = Str(o, key); break; }
            }
            return Explain(m.Trim(), status, text);
        }

        // Adds plain-language context to known problems and turns empty or non-JSON errors into a sentence.
        static string Explain(string m, int status, string raw)
        {
            if (m.IndexOf("Expected badge cost is different", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The badge price changed (the free daily count was probably used up elsewhere), so Roblox stopped and nothing was charged. Click Create all again to see the new price.";
            if (m.IndexOf("Insufficient funds", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Not enough Robux to pay for this badge. Nothing was charged.";
            if (m.IndexOf("moderated", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Roblox's text filter blocked this name or description. Try different wording.";
            if (m != "" && status != 429 && status < 500) return m;

            if (status == 429)
                return "Roblox is limiting how fast requests can be made. Wait a minute, then try again." + (m != "" ? " (" + m + ")" : "");
            if (status >= 500)
                return "Roblox had a problem on its side (HTTP " + status + "). Try again in a moment." + (m != "" && !LooksLikeHtml(m) ? " (" + m + ")" : "");
            if (status == 401 || status == 403)
                return "Roblox refused this (" + status + "). You may not have permission to manage this game, or the app is missing a permission scope.";
            if (status == 404)
                return "Roblox couldn't find this (404). It may have been deleted, or it belongs to a different game.";
            string snippet = (raw ?? "").Trim();
            if (LooksLikeHtml(snippet)) snippet = "";
            if (snippet.Length > 200) snippet = snippet.Substring(0, 200) + "...";
            return "Roblox returned an error (HTTP " + status + ")" + (snippet != "" ? ": " + snippet : ".");
        }

        static bool LooksLikeHtml(string s)
        {
            s = (s ?? "").TrimStart();
            return s.StartsWith("<", StringComparison.Ordinal);
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
                    "Close that program (for example an older copy of this app), or choose another port in Advanced settings (the Redirect URL on Roblox must match it).");
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
        static readonly string OAuth = TestRoot + "/oauth/v1", Apis = TestRoot, BadgesApi = TestRoot + "/badges", GamesApi = TestRoot + "/games", ThumbsApi = TestRoot + "/thumbs", ItemConfigApi = TestRoot + "/itemconfig";
#else
        const string OAuth = "https://apis.roblox.com/oauth/v1";
        const string Apis = "https://apis.roblox.com";
        const string BadgesApi = "https://badges.roblox.com";
        const string GamesApi = "https://games.roblox.com";
        const string ThumbsApi = "https://thumbnails.roblox.com";
        const string ItemConfigApi = "https://itemconfiguration.roblox.com"; // Marketplace items (API key only)
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
                (chooseGames ? "&prompt=" + E("select_account consent") : ""); // Roblox requires account selection alongside consent

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

        // For anything that changes data, a timeout means Roblox may have done it anyway.
        static ApiException MaybeDone(ApiException ex, HttpMethod method)
        {
            if (method == HttpMethod.Get) return ex;
            var e = new ApiException(0, "No answer from Roblox, so it may or may not have gone through. Reload and check before trying again.");
            e.Uncertain = true;
            return e;
        }

        // A Client Secret is sent only if one is set. Without one the app is a public client:
        // the PKCE code_verifier proves the sign-in, as Roblox's OAuth docs describe for apps that can't keep secrets.
        async Task<object> OAuthPost(string path, Dictionary<string, string> form)
        {
            form["client_id"] = settings.ClientId;
            bool withSecret = settings.ClientSecret != "";
            if (withSecret) form["client_secret"] = settings.ClientSecret;
            HttpResponseMessage resp;
            try { resp = await http.PostAsync(OAuth + path, new FormUrlEncodedContent(form)); }
            catch (HttpRequestException) { throw Offline(); }
            catch (TaskCanceledException) { throw TooSlow(); }
            using (resp)
            {
                int status = (int)resp.StatusCode;
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    object err = null;
                    try { err = J.Parse(text); } catch (Exception) { }
                    string code = J.Str(err, "error");
                    if (code == "invalid_client" || code == "unauthorized_client")
                        throw new ApiException(status, withSecret
                            ? "Roblox rejected the Client Secret saved in Settings. Fix it there, or clear it to sign in without one."
                            : "Roblox requires a Client Secret for this OAuth app, so signing in without one isn't possible. The app's owner can enter it in Settings.");
                    throw new ApiException(status, "Roblox sign-in error (" + status + "): " + J.Describe(text, status));
                }
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
                    catch (HttpRequestException) { throw MaybeDone(Offline(), method); }
                    catch (TaskCanceledException) { throw MaybeDone(TooSlow(), method); }
                    using (resp)
                    {
                        int status = (int)resp.StatusCode;
                        if (status == 429 && attempt < 3) { await Task.Delay(RetryDelay(resp, attempt)); continue; }
                        if (status >= 500 && method == HttpMethod.Get && attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(attempt)); continue; } // temporary Roblox trouble: reads are safe to retry
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
                    var d = await BadgePage(BadgesApi + "/v1/universes/" + universe + "/badges?limit=100&sortOrder=Asc" +
                        (token != "" ? "&cursor=" + E(token) : ""));
                    foreach (var b in J.A(d, "data"))
                        list.Add(new Item { Id = J.Str(b, "id"), Name = J.Str(b, "name"), Description = J.Str(b, "description"), Enabled = J.B(b, "enabled"), IconAssetId = J.Str(b, "iconImageId") });
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
                            IconAssetId = J.Str(x, "iconImageAssetId"),
                            Managed = J.B(x, "isManagedPricingEnabled"),
                        });
                    }
                    token = J.Str(d, "nextPageToken");
                }
                if (token == "") break;
            }
            if (kind == Kind.Badge) await AddHiddenBadges(universe, list);
            return list;
        }

        // Public badge list. (Tested: Roblox ignores an OAuth token here, so disabled badges never appear in it.)
        Task<object> BadgePage(string url) { return Send(HttpMethod.Get, url, null, false); }

        // Roblox's public badge list leaves out disabled badges. Look up remembered IDs that are missing.
        async Task AddHiddenBadges(string universe, List<Item> list)
        {
            var known = settings.KnownBadges(universe);
            var listed = new HashSet<string>(list.Select(i => i.Id));
            bool removed = false;
            foreach (var id in known.Where(id => !listed.Contains(id)).ToList())
            {
                try { list.Add(await GetBadge(universe, id)); }
                catch (ApiException ex) { if (ex.Status == 404 || ex.Status == 400) { known.Remove(id); removed = true; } } // deleted or not in this game
            }
            int before = known.Count;
            foreach (var i in list) known.Add(i.Id);
            list.Sort((a, b) => CompareIds(a.Id, b.Id));
            if (known.Count != before || removed) TrySave(); // only write the settings file when something changed
        }

        static int CompareIds(string a, string b)
        {
            long x, y;
            if (long.TryParse(a, out x) && long.TryParse(b, out y)) return x.CompareTo(y);
            return string.CompareOrdinal(a, b);
        }

        // Single badge lookup (works for disabled badges too). Fails with 400 if the badge belongs to another game.
        public async Task<Item> GetBadge(string universe, string id)
        {
            var b = await Send(HttpMethod.Get, BadgesApi + "/v1/badges/" + E(id), null, false);
            string owner = J.Str(J.G(b, "awardingUniverse"), "id");
            if (owner != "" && owner != universe) throw new ApiException(400, "Badge " + id + " belongs to a different game.");
            return new Item { Id = J.Str(b, "id"), Name = J.Str(b, "name"), Description = J.Str(b, "description"), Enabled = J.B(b, "enabled") };
        }

        public void RememberBadge(string universe, string id)
        {
            if (id == "") return;
            settings.KnownBadges(universe).Add(id);
            TrySave();
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
                    if (n.Managed != "") f.Add("isManagedPricingEnabled", n.Managed); // Managed pricing includes regional pricing
                }
                if (image != null) f.AddFile(kind == Kind.Badge ? "files" : "imageFile", n.ImagePath, image);
                return f.Content;
            }, true);
            string newId = J.Str(r, kind == Kind.Pass ? "gamePassId" : kind == Kind.Product ? "productId" : "id");
            if (kind == Kind.Badge) RememberBadge(u.Id, newId);
            return newId;
        }

        // Reads one pass/product's pricing back from Roblox (Regional and Managed are filled in).
        // Roblox can answer "success" to a change it didn't apply, so the app checks.
        public async Task<Item> ReadPricing(Kind kind, string universe, string id)
        {
            string url = kind == Kind.Pass
                ? Apis + "/game-passes/v1/universes/" + universe + "/game-passes/" + id + "/creator"
                : Apis + "/developer-products/v2/universes/" + universe + "/developer-products/" + id + "/creator";
            var x = await Send(HttpMethod.Get, url, null, true);
            return new Item
            {
                Id = id,
                Regional = J.A(J.G(x, "priceInformation"), "enabledFeatures").Any(f => Convert.ToString(f) == "RegionalPricing"),
                Managed = J.B(x, "isManagedPricingEnabled"),
            };
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
                if (ch.Managed.HasValue) f.Add("isManagedPricingEnabled", Bool(ch.Managed.Value));
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

        // ---------- Marketplace items (Clothing & Accessories tab; Roblox API key, not OAuth) ----------
        // These itemconfiguration endpoints only accept an API key (x-api-key) with "Marketplace items" permissions.
        public bool HasApiKey { get { return settings.ApiKey != ""; } }

        async Task<object> SendWithKey(HttpMethod method, string url, string json)
        {
            if (settings.ApiKey == "") throw new ApiException(0, "Set your Roblox API key first (Set API key).");
            for (int attempt = 1; ; attempt++)
            {
                using (var req = new HttpRequestMessage(method, url))
                {
                    req.Headers.Add("x-api-key", settings.ApiKey);
                    if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    HttpResponseMessage resp;
                    try { resp = await http.SendAsync(req); }
                    catch (HttpRequestException) { throw MaybeDone(Offline(), method); }
                    catch (TaskCanceledException) { throw MaybeDone(TooSlow(), method); }
                    using (resp)
                    {
                        int status = (int)resp.StatusCode;
                        if (status == 429 && attempt < 3) { await Task.Delay(RetryDelay(resp, attempt)); continue; }
                        if (status >= 500 && method == HttpMethod.Get && attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(attempt)); continue; }
                        string text = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode) throw new ApiException(status, KeyError(J.Describe(text, status), status));
                        try { return J.Parse(text); } catch (ArgumentException) { return text; }
                    }
                }
            }
        }

        static string KeyError(string message, int status)
        {
            if (message.IndexOf("Invalid API Key", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Roblox rejected the API key. Check it under Set API key, or create a new one (Marketplace items: Read and Write).";
            if (message.IndexOf("IP", StringComparison.Ordinal) >= 0 && message.IndexOf("not", StringComparison.OrdinalIgnoreCase) >= 0)
                return message + " Your internet address may have changed: update Accepted IP Addresses on the key, or turn IP restriction off.";
            if (status == 401 || status == 403)
                return message + " The key may be missing the Marketplace items permission (Read to list, Write to edit), or your group role can't manage this group's items.";
            return message;
        }

        static string CreatorQuery(string groupId) { return groupId != "" ? "&groupId=" + Uri.EscapeDataString(groupId) : ""; }

        // Lists items of the given asset types (e.g. 2 T-Shirt, 11 Shirt, 12 Pants, 8 Hat...) for you or a group.
        public async Task<List<MarketItem>> ListMarketItems(IEnumerable<int> assetTypes, string groupId)
        {
            var list = new List<MarketItem>();
            foreach (int type in assetTypes)
            {
                string cursor = "";
                for (int page = 0; page < 100; page++)
                {
                    var d = await SendWithKey(HttpMethod.Get, ItemConfigApi + "/v1/items/by-creator?assetType=" + type + CreatorQuery(groupId) +
                        (cursor != "" ? "&cursor=" + E(cursor) : ""), null);
                    foreach (var x in J.A(d, "items"))
                    {
                        long sale = J.L(x, "saleStatus") ?? 1;
                        list.Add(new MarketItem
                        {
                            Id = J.Str(x, "id"),
                            Name = J.Str(x, "name"),
                            Description = J.Str(x, "description"),
                            AssetType = (int)(J.L(J.G(J.G(x, "marketplaceItemDetails"), "assetDetails"), "assetType") ?? type),
                            Price = J.L(x, "price"),
                            OnSale = sale == 0 || sale == 2,
                            Free = sale == 2,
                            CollectibleItemId = J.Str(J.G(x, "collectibleDetails"), "collectibleItemId"),
                            Resellable = J.B(J.G(x, "collectibleDetails"), "isResellable"),
                            Moderation = ModerationName(J.L(x, "moderationStatus")),
                        });
                    }
                    cursor = J.Str(d, "nextCursor"); if (cursor == "") cursor = J.Str(d, "nextPageCursor");
                    if (cursor == "") break;
                }
            }
            return list;
        }

        static string ModerationName(long? s)
        {
            switch (s ?? 0) { case 1: return "Pending"; case 2: return "Moderated"; case 3: return "Approved"; case 4: return "Needs attention"; default: return ""; }
        }

        // Name and description (both are always sent).
        public async Task UpdateMarketDisplay(string assetId, string name, string description)
        {
            // Roblox answers "Missing/Null request parameters" unless both name and description are sent, so always send both.
            var d = new Dictionary<string, object> { { "targetType", 0 }, { "targetId", assetId }, { "name", name ?? "" }, { "description", description ?? "" } };
            await SendWithKey(new HttpMethod("PATCH"), ItemConfigApi + "/v1/collectibles/update-item-display-information", J.S.Serialize(d));
        }

        // Price and/or sale status of an item published into the collectible system.
        public async Task UpdateMarketSale(string collectibleItemId, bool onSale, long? price, bool resellable)
        {
            // Tested against Roblox: it answers "Missing/Null request parameters" without saleLocationConfiguration and
            // "Invalid resale restriction" without resaleRestriction. Sale location 1 = catalog and all experiences.
            var d = new Dictionary<string, object>
            {
                { "saleStatus", onSale ? 0 : 1 },
                { "saleLocationConfiguration", new Dictionary<string, object> { { "saleLocationType", 1 }, { "places", new long[0] } } },
                { "resaleRestriction", resellable ? 1 : 2 }, // 1 = resale allowed, 2 = disabled: keeps the current setting
            };
            // Always send the free flag and price explicitly; Roblox rejects some requests with "Missing/Null request parameters".
            bool free = price.HasValue && price.Value == 0;
            d["isFree"] = free;
            if (!free && price.HasValue) d["priceInRobux"] = price.Value;
            await SendWithKey(new HttpMethod("PATCH"), ItemConfigApi + "/v1/collectibles/" + E(collectibleItemId), J.S.Serialize(d));
        }

        // ---------- thumbnails (public, no sign-in needed) ----------
        // ids: game pass IDs, badge IDs, or (developer products) icon image asset IDs. Returns id -> image URL.
        public async Task<Dictionary<string, string>> IconUrls(Kind kind, IList<string> ids)
        {
            var urls = new Dictionary<string, string>();
            for (int i = 0; i < ids.Count; i += 100)
            {
                string list = string.Join(",", ids.Skip(i).Take(100));
                string url = kind == Kind.Pass ? ThumbsApi + "/v1/game-passes?gamePassIds=" + list
                    : kind == Kind.Badge ? ThumbsApi + "/v1/badges/icons?badgeIds=" + list
                    : ThumbsApi + "/v1/assets?assetIds=" + list;
                var d = await Send(HttpMethod.Get, url + "&size=150x150&format=Png", null, false);
                foreach (var t in J.A(d, "data"))
                    if (J.Str(t, "state") == "Completed" && J.Str(t, "imageUrl") != "") urls[J.Str(t, "targetId")] = J.Str(t, "imageUrl");
            }
            return urls;
        }

        // One large icon for the hover preview. Asset images come in 420x420; game pass and badge thumbnails only in 150x150.
        public async Task<string> LargeIconUrl(Kind kind, string id)
        {
            string url = kind == Kind.Pass ? ThumbsApi + "/v1/game-passes?gamePassIds=" + E(id) + "&size=150x150"
                : kind == Kind.Badge ? ThumbsApi + "/v1/badges/icons?badgeIds=" + E(id) + "&size=150x150"
                : ThumbsApi + "/v1/assets?assetIds=" + E(id) + "&size=420x420";
            var d = await Send(HttpMethod.Get, url + "&format=Png", null, false);
            var first = J.A(d, "data").FirstOrDefault();
            return J.Str(first, "state") == "Completed" ? J.Str(first, "imageUrl") : "";
        }

        public async Task<string> HeadshotUrl(string userId)
        {
            var d = await Send(HttpMethod.Get, ThumbsApi + "/v1/users/avatar-headshot?userIds=" + E(userId) + "&size=48x48&format=Png&isCircular=true", null, false);
            var first = J.A(d, "data").FirstOrDefault();
            return J.Str(first, "state") == "Completed" ? J.Str(first, "imageUrl") : "";
        }

        // Downloads a file to a temporary path (used to re-upload an existing icon). Returns null on failure.
        public async Task<string> DownloadToTemp(string url, string name)
        {
            try
            {
                byte[] bytes = await http.GetByteArrayAsync(url);
                string path = Path.Combine(Path.GetTempPath(), "rpm-" + name + ".png");
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception) { return null; }
        }

        // Downloads an image and shrinks it to size x size pixels so it uses little memory. Returns null on failure.
        public async Task<System.Drawing.Image> DownloadImage(string url, int size)
        {
            try
            {
                byte[] bytes = await http.GetByteArrayAsync(url);
                using (var ms = new MemoryStream(bytes))
                using (var src = System.Drawing.Image.FromStream(ms))
                {
                    var bmp = new System.Drawing.Bitmap(size, size);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, size, size);
                    }
                    return bmp;
                }
            }
            catch (Exception) { return null; }
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
