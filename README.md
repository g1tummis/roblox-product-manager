# Roblox Product Manager

A deliberately plain **Windows app** for creating and editing **game passes**, **developer products** and **badges** on your Roblox experiences, one at a time or many at once. You **Sign in with Roblox**, pick one of your experiences (or a group experience), and work in spreadsheet-style grids.

- **Lightweight:** one exe of about 100 KB that uses the .NET Framework built into Windows 10 and 11. Measured on Windows 11: about 16 MB of private memory when freshly opened, and about 26 MB signed in with all three tabs loaded. CPU stays at 0% while idle. Nothing to install.
- **Follows Roblox's OAuth 2.0 protocol:** authorization code flow with PKCE (S256), `state` and `nonce`. You sign in in your own web browser, and the app receives the result on `http://localhost:3000/callback`. See [How this follows Roblox's OAuth 2.0 protocol](#how-this-follows-robloxs-oauth-20-protocol).
- There's also an optional [web version](#web-version-optional) (`server.js` + `index.html`) for hosting on a server.

---

## Part 1 — One-time setup

### Step 1: Requirements
- Windows 10 or 11.
- A Roblox account that is **ID verified**. Roblox requires this to register OAuth 2.0 apps.

### Step 2: Register an OAuth 2.0 app on Roblox
1. Open the Creator Dashboard: **[OAuth 2.0 Apps](https://create.roblox.com/dashboard/credentials?activeTab=OAuthTab)**.
2. Click **Create App**, enter a unique name, agree to the Terms, then click **Create**.
3. **Copy the Client ID and the Secret now.** Roblox won't show the secret again; you can only generate a new one.
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

   If you tick a different set, enter the same set in the app's **Settings** window. The app asks for exactly those scopes, and Roblox rejects any scope your app isn't registered for.
3. **Redirect URL:** add `http://localhost:3000/callback`. If you change the port in the app's settings, add that port's URL instead.
4. Save.

### Step 4: Run the app
1. Double-click **`RobloxProductManager.exe`**. Get it from the GitHub release, or build it yourself (see below).
2. The first time, a setup window asks for your **Client ID** and **Client Secret**. Leave **Port** at 3000 unless something else uses it. The window also shows the Redirect URL and scopes, each with a **Copy** button.
3. Click **Sign in with Roblox**.

Your Client ID and Secret are saved in `%APPDATA%\RobloxProductManager\app-settings.json`. The secret and your Roblox sign-in are encrypted with Windows DPAPI, so only your Windows account can read them. Delete that file to reset the app.

Windows may warn that the publisher is unknown, because the exe isn't code-signed.

### Step 5: Private mode vs. public
A new OAuth app is in **private mode**, limited to **10 unique users**. That's enough for you and your team. To open it to everyone, submit it for review from the OAuth 2.0 Apps page (**Edit and Publish**). Roblox requires:
- A description, a thumbnail (150x150 or larger), and an **Entry Link** (for example this project's page).
- A **Privacy Policy URL** and a **Terms of Service URL**. Both must be HTTPS and at most 256 characters. You must write and host these yourself.
- A public **demo video** of 1 minute or less. It must start signed out, show the user clicking **Sign in with Roblox**, the full Roblox consent screen, and the return to the app.
- A justification for the category and each scope. Roblox doesn't approve scopes for planned future features.

---

## Part 2 — What players do (also shown in the app)

1. Click **Sign in with Roblox**. Their web browser opens Roblox's own page (apis.roblox.com). The app never sees their password.
2. Log in to Roblox there if asked. A 13+ account is required.
3. Read the permissions Roblox lists, then **choose which experiences** the app may manage. These can be their own experiences, or group experiences their group role is allowed to manage.
4. Confirm, then come back to the app window. They're signed in.
5. Pick a game, pick a tab (**Game Passes**, **Developer Products**, **Badges**), then create or edit.

They stay signed in the next time they open the app. To share a different game, click **Add or remove games**, which reopens Roblox's consent screen. **Sign out** revokes the app's access on Roblox.

---

## Using the app

**Create many at once.** The top grid of each tab is for new items.
- Type into the grid like a spreadsheet.
- Or copy rows from Excel or Google Sheets and press **Ctrl+V** (or click **Paste rows**). Columns: `Name, Price, Description` (badges: `Name, Description`). A header row is skipped. Lines like `VIP | 199 | Double coins` work too.
- Or drop image files onto the grid to get one row per image, named after each file.
- Set each row's image with the **...** button, or set **Image for rows without one** once for all of them.
- Click **Create all**. Rows are created one by one, and each row shows its result and new ID. Rows that fail stay editable with Roblox's error message: fix them and click **Create all** again. Created rows are skipped and greyed out.
- Draft rows stay when you switch games, so you can create the same set in several games.

**Edit.** The bottom grid shows what already exists. Edit any cell, or drop a new image onto a row. Changed rows turn yellow. Click **Save changes**, and the grid reloads from Roblox so it shows what was really saved. Double-click an ID (or click **Open on Roblox**) to open the item's Roblox page. Select cells and press Ctrl+C to copy IDs.

**Badges and Robux.** Each game gets a few free badges per day (UTC). After that, each badge costs 100 Robux, paid from your account (or the group's funds for a group game). The app shows the free count and asks before creating any badge that costs Robux. It also sends Roblox the `expectedCost`, so Roblox refuses the request if the real price is different.

**Limits.** Badge icons can't be changed through this API after creation; use the Creator Dashboard. Developer products that Roblox marks as immutable are shown read-only.

---

## Build the app yourself

Double-click **`build.cmd`**, or run:
```bash
build.cmd
```
It compiles `app\*.cs` with the C# compiler that ships with Windows (`csc.exe` from .NET Framework 4.x) into `dist\RobloxProductManager.exe`. Nothing is downloaded or installed.

---

## Web version (optional)

`server.js` and `index.html` are a dependency-free web version of the same tool, for hosting on a server so people can use it in a browser. It needs [Node.js](https://nodejs.org) 18 or newer.

1. Copy `.env.example` to `.env`. Fill in `ROBLOX_CLIENT_ID`, `ROBLOX_CLIENT_SECRET` and `REDIRECT_URI` (for example `https://your-domain/callback`, which must be registered on your Roblox app). Without a `.env`, `node server.js` asks for these in the terminal.
2. Run:
   ```bash
   node server.js
   ```
3. Put it behind HTTPS (for example Caddy or nginx as a reverse proxy to `PORT`). Sign-ins are kept in memory, so restarting signs everyone out. Run a single process.

As a confidential client, the web version keeps the client secret on the server. The browser can only reach an allow-list of Open Cloud endpoints, and only for granted experiences. Every API call needs a custom header, the cookie is HttpOnly and `SameSite=Lax`, and the page has a strict, hash-pinned Content-Security-Policy.

---

## How this follows Roblox's OAuth 2.0 protocol

Roblox's [OAuth 2.0 overview](https://create.roblox.com/docs/cloud/auth/oauth2-overview) defines four roles:

| Role | In this app |
|---|---|
| Resource owner | The player who signs in and picks experiences on Roblox's consent screen |
| Client | Roblox Product Manager, using the OAuth app you registered (your Client ID and Secret) |
| Authorization server | `https://apis.roblox.com/oauth` |
| Resource server | Roblox Open Cloud: game passes, developer products, and legacy badges APIs |

The flow is the **authorization code flow with PKCE**, which Roblox recommends for all clients:
1. **Sign in with Roblox** creates a random `code_verifier` (64 characters), its S256 `code_challenge`, a `state` and a `nonce`.
2. The app starts a listener on `http://localhost:3000/callback`, then opens your browser at `GET https://apis.roblox.com/oauth/v1/authorize`. The request carries `client_id`, `redirect_uri`, `scope`, `response_type=code`, `state`, `nonce`, `code_challenge` and `code_challenge_method=S256`. Signing in through the system browser, not a window inside the app, is the standard for desktop apps, and means the app never sees your Roblox password.
3. Roblox authenticates you and collects consent, then redirects to the callback with `code` and `state`.
4. The app checks `state`, then redeems the code once at `POST /oauth/v1/token`. It sends `code`, `code_verifier`, `client_id` and `client_secret`, form-encoded.
5. The ID token's `nonce` is checked.
6. `POST /oauth/v1/token/resources` lists the experiences you granted. Only those appear in the app.
7. Access tokens (15 minutes) are refreshed with the single-use refresh token (90 days). Refreshes are serialized so a refresh token is never used twice. The newest refresh token is saved encrypted, which is how you stay signed in.
8. **Sign out** calls `POST /oauth/v1/token/revoke` and deletes the saved sign-in.

## Files

| File | What it is |
|---|---|
| `app\*.cs` | The Windows app (C#, WinForms) |
| `app\app.manifest` | Windows manifest (high-DPI support, no admin rights needed) |
| `build.cmd` | Builds `dist\RobloxProductManager.exe` |
| `server.js`, `index.html` | The optional web version |
| `.env.example` | Settings template for the web version |

The app's C# code contains test hooks used only when it is compiled with `/define:TESTHOOKS`. These point the app at a local fake Roblox server for automated testing. `build.cmd` never sets that flag, so the real exe doesn't contain them.
