# Roblox Product Manager

A deliberately plain web app. Players **Sign in with Roblox**, pick one of their experiences (or a group experience), then create and edit **game passes**, **developer products** and **badges** — one at a time or many at once.

- **Lightweight:** one Node.js file and one HTML file. No dependencies, no database, and no `npm install`. Memory use is about that of a bare Node process (about 55 MB measured on Windows). CPU stays idle unless a request is being handled.
- **Windows .exe:** `RobloxProductManager.exe` runs on its own, with no Node install needed. On first run it asks for your Client ID and Client Secret, then opens the app in your browser. See [Run it as a Windows .exe](#run-it-as-a-windows-exe).
- **Roblox's OAuth 2.0 protocol:** authorization code flow with PKCE (S256), `state` and `nonce`, as a confidential client. See [How this follows Roblox's OAuth 2.0 protocol](#how-this-follows-robloxs-oauth-20-protocol).

---

## Part 1 — App owner setup (one time)

### Step 1: Requirements
- A Roblox account that is **ID verified**. Roblox requires this to register OAuth 2.0 apps.
- To use the .exe: Windows 10 or 11 (64-bit). Nothing else.
- To run from source or build the .exe: [Node.js](https://nodejs.org) 18 or newer (20.12 or newer to build).

### Step 2: Register the OAuth 2.0 app on Roblox
1. Open the Creator Dashboard: **[OAuth 2.0 Apps](https://create.roblox.com/dashboard/credentials?activeTab=OAuthTab)**.
2. Click **Create App**, enter a unique name, agree to the Terms, then click **Create**.
3. **Copy the Client ID and the Secret now.** Roblox will not show the secret again. You can only generate a new one.
4. Click **Continue to Edit**.

### Step 3: Configure the app on Roblox
1. **App Category:** `Creation & Productivity Tools`.
2. **Permission Scopes.** Tick exactly these:

   | Scope | Why |
   |---|---|
   | `openid` | Identify who signed in (required) |
   | `profile` | Show their username and display name |
   | `game-pass:read` | List game passes |
   | `game-pass:write` | Create and edit game passes |
   | `developer-product:read` | List developer products |
   | `developer-product:write` | Create and edit developer products |
   | `legacy-universe.badge:manage-and-spend-robux` | Create badges (can spend Robux after the free daily quota) and edit them |

   If you tick a different set, put the same set in `SCOPES` in your `.env` file (or `"scopes"` in the .exe's settings file). The app asks for exactly what `SCOPES` says, and Roblox rejects scopes the app was not registered for.
3. **Redirect URLs:** add
   - `http://localhost:3000/callback` for running on your own PC, and/or
   - `https://YOUR-DOMAIN/callback` for a hosted site (it must be HTTPS).
4. Save.

### Step 4: Start the app

#### Run it as a Windows .exe
1. Double-click `RobloxProductManager.exe`. If you built it yourself, it's in `dist\`.
2. The first time, its window asks for:
   - **Client ID**: paste it and press Enter.
   - **Client Secret**: paste it and press Enter. It shows as `*****`.
   - **Port**: press Enter to keep `3000`.
3. It saves them to `%APPDATA%\RobloxProductManager\settings.json` and shows the exact **Redirect URL** that must be on your Roblox app. For port 3000 that's `http://localhost:3000/callback`.
4. Your browser opens the app. **Keep the window open while you use it. Close the window to stop the app.**

On later runs it starts with your saved settings. To change them, press **S** when it says "Press S to change them", or run `RobloxProductManager.exe --setup`.

> The settings file holds your Client Secret in plain text, inside your Windows user folder. Don't share it. Delete it to reset the app. Advanced: add `"scopes": "..."` or `"redirectUri": "..."` to it to override those defaults.

#### Build the .exe yourself
```bash
node build.js
```
This writes `dist\RobloxProductManager.exe` (about 88 MB, because it contains its own copy of Node.js). It uses Node's built-in single-executable feature and downloads nothing. Rebuild after changing `server.js` or `index.html`. Windows may warn that the publisher is unknown, because the exe isn't code-signed.

#### Or run from source (developers)
1. Copy `.env.example` to `.env`, then fill in `ROBLOX_CLIENT_ID`, `ROBLOX_CLIENT_SECRET` and `REDIRECT_URI` (exactly as registered in Step 3). You can also skip `.env` and answer the prompts as with the .exe.
2. Run:
   ```bash
   node server.js
   ```
3. Open **http://localhost:3000**. Use `localhost`, not `127.0.0.1`: the sign-in cookie must match the redirect URL's host. The app fixes this for you on `/login`.

### Step 5: Private mode vs. public
A new OAuth app is in **private mode**, limited to **10 unique users**. That is enough for you and your team. To open it to everyone, submit it for review from the OAuth 2.0 Apps page (**Edit and Publish**). Roblox requires:
- A description, a thumbnail (150x150 or larger), and an **Entry Link** (your site's home page).
- A **Privacy Policy URL** and a **Terms of Service URL**. Both must be HTTPS and at most 256 characters. You must write and host these yourself.
- A public **demo video** of 1 minute or less. It must start signed out, show the user clicking **Sign in with Roblox**, the full Roblox consent screen, and the redirect back into the app.
- A justification for the category and each scope. Roblox does not approve scopes for planned future features.

### Hosting notes
- Set `REDIRECT_URI=https://your-domain/callback` and put the app behind HTTPS (for example Caddy or nginx as a reverse proxy to `PORT`).
- Sign-ins are kept in memory. Restarting the server signs everyone out. Run a single process.
- Keep `.env` private. Anyone with the client secret can pretend to be your app.

---

## Part 2 — What players do (also shown on the sign-in page)

1. Click **Sign in with Roblox**. They go to Roblox's own page (apis.roblox.com). The app never sees their password.
2. Log in to Roblox if asked. A 13+ account is required.
3. Read the permissions Roblox lists, then **choose which experiences** the app may manage. This can include their own experiences and group experiences their group role is allowed to manage.
4. Confirm. Roblox sends them back to the app, signed in.
5. Pick a game, pick a tab (**Game Passes**, **Developer Products**, **Badges**), then create or edit.

To share a different game later, click **Add or remove games**. This reopens Roblox's consent screen. **Sign out** revokes the app's access on Roblox.

---

## Using the app

**Multi-create.** Each tab has a table of new rows.
- Paste lines into **Quick add**: `Name | Price | Description` (badges: `Name | Description`), then click **Add these rows**. You can also click **Add empty row**.
- Give rows their own image, or set **Image for rows that have none** once for all of them.
- Click **Create all**. Rows are created one by one, and each row shows its result and new ID. Rows that fail stay editable, so you can fix them and click **Create all** again. Created rows are skipped.
- Draft rows stay when you switch games, so you can create the same set in several games.

**Edit.** The **Existing** table is editable in place. Changed rows turn yellow. Click **Save changes** to save them all. The table then reloads from Roblox so it shows what was really saved.

**Badges and Robux.** Each game gets a number of free badges per day (UTC). After that, each badge costs 100 Robux, paid from your account (or group funds for a group game). The app shows the free count and asks before creating any badge that costs Robux. It also sends Roblox the `expectedCost`, so Roblox refuses the request if the real price differs.

**Limits.** Badge icons can't be changed through this API after creation; change them in the Creator Dashboard. Developer products that Roblox marks as immutable are shown read-only.

---

## How this follows Roblox's OAuth 2.0 protocol

Roblox's [OAuth 2.0 overview](https://create.roblox.com/docs/cloud/auth/oauth2-overview) defines four roles. Here is how they map to this app:

| Role | In this app |
|---|---|
| Resource owner | The player who signs in and picks experiences on Roblox's consent screen |
| Client | `server.js`, a **confidential client**: the client secret stays on the server and never reaches the browser |
| Authorization server | `https://apis.roblox.com/oauth` |
| Resource server | Roblox Open Cloud: game passes, developer products, and legacy badges APIs |

The flow is the **authorization code flow with PKCE**, which Roblox recommends for all clients:
1. `/login` creates a random `code_verifier` (64 characters), its S256 `code_challenge`, a `state` and a `nonce`. They are stored in a server-side session tied to an HttpOnly cookie.
2. The browser goes to `GET https://apis.roblox.com/oauth/v1/authorize` with `client_id`, `redirect_uri`, `scope`, `response_type=code`, `state`, `nonce`, `code_challenge` and `code_challenge_method=S256`.
3. Roblox authenticates the user and collects consent. It then redirects to `REDIRECT_URI` with `code` and `state`.
4. The server checks `state`, then redeems the code once at `POST /oauth/v1/token`. It sends `code`, `code_verifier`, `client_id` and `client_secret`, form-encoded. Codes expire after 1 minute.
5. The ID token's `nonce` is checked. The session ID is rotated.
6. `POST /oauth/v1/token/resources` lists the experiences the user granted. Only those can be used.
7. Access tokens (15 minutes) are refreshed with the single-use refresh token (90 days). Refreshes are serialized per session so a refresh token is never used twice.
8. **Sign out** calls `POST /oauth/v1/token/revoke`.

Other safeguards:
- The browser can only reach an allow-list of Open Cloud endpoints, and only for granted experiences.
- Every API call requires a custom header (blocks cross-site requests). The cookie is `SameSite=Lax`, plus `Secure` on HTTPS.
- The page has a strict Content-Security-Policy with a hash-pinned script and cannot be framed.

## Files

| File | What it is |
|---|---|
| `server.js` | OAuth, sessions, the Open Cloud proxy, and the first-run setup prompts |
| `index.html` | The whole UI: plain HTML and a small inline script |
| `build.js` | Builds `dist\RobloxProductManager.exe` |
| `.env.example` | Settings template for running from source. Copy it to `.env` |
