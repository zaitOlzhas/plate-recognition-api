# CLAUDE.md

ASP.NET Core (.NET 10) minimal API that receives `plate_recognized` webhooks from an LPR system, stores them in
SQLite, and serves an events viewer. An optional Telegram bot pulses a relay (gate) via `/open_gate`, gated by
per-user permissions that admins manage from Telegram or the web Access tab.
User-facing docs are in `README.md`; keep it in sync when behavior or config changes.

## Commands

```sh
dotnet build                                  # solution: LprWebhook.slnx
dotnet test                                   # xUnit tests in tests/LprWebhook.Api.Tests
docker build --target test .                  # run tests without a local SDK
docker compose up -d --build                  # build image and (re)start container on :5080
docker compose logs -f lpr-webhook
```

On Windows, Docker Desktop must be running (error mentioning `dockerDesktopLinuxEngine` pipe = engine is down).

## Layout

- `src/LprWebhook.Api/Program.cs` — startup; `--healthcheck` arg runs `HealthProbe` (runtime image has no curl).
  Ends with `public partial class Program;` for `WebApplicationFactory` in the tests.
- `Endpoints/EventEndpoints.cs` — `POST /api/events/plate`, `GET /api/events`, `GET /api/events/{id}`.
- `Endpoints/BotCommandEndpoints.cs` — `GET /api/bot-commands` (Telegram bot message log).
- `Endpoints/AuthEndpoints.cs` — admin web sign-in: `/weblogin` code → `GET /login?code=` or `POST /api/auth/login`
  → `lpr_admin` cookie (SameSite=Strict; that is the CSRF protection). `GET /api/auth/me`, `POST /api/auth/logout`.
- `Endpoints/AccessEndpoints.cs` — `/api/access/*` admin API; a group endpoint filter re-checks the admin role in the
  DB on every request (the cookie only proves identity).
- `Access/AccessService.cs` — the single place for role/permission rules and audit writes; both the bot and the web
  API call it. Returns `AccessResult` (Ok/Denied/Invalid) instead of throwing, and audits refused attempts too.
- `Access/BotPermissions.cs` — catalog of grantable commands. New command = catalog entry + case in `BotCommandHandler`.
- `Access/LoginCodeStore.cs` — single-use 5-minute codes in `IMemoryCache` (lost on restart, by design).
- `Parsing/` — deliberately lenient JSON/timestamp parsing; vendor payloads can be malformed (e.g. `"number": ,`).
  Don't make it stricter without tests covering the vendor quirks listed in the README.
- `Data/` — `PlateEvent`, `BotCommandLog`, `BotUser`/`UserPermission`/`PermissionAudit`, `EventsDbContext`.
  No migrations: startup runs the EF create script with `IF NOT EXISTS`, so new tables appear in existing DBs but new
  columns on existing tables do not. That's why decisions live in `plate_decisions` (1:1 with `plate_events`), not
  in new columns.
- `Configuration/TelegramBotOptions.cs` — bound from the `TelegramBot` config section (incl. `OwnerUserId`, `WebBaseUrl`).
- `Services/TelegramBotService.cs` — hosted service: long polling, upserts the sender into `bot_users`, saves every
  text message to `bot_commands` (a failed save is logged and must never break the bot), sets the command menu.
- `Services/BotCommandHandler.cs` — scoped, one method per command; sets the record's outcome
  (`success`/`failed`/`error`/`denied`/`ignored`). Replies are plain text (no parse mode) on purpose.
- `Services/RelayService.cs` — HTTP POST to the relay. Bot services are registered only when `TelegramBot:Enabled`.
- `Services/PlateDeduplicator.cs` — singleton, in-memory: same plate + camera within `PlateEvents:DuplicateWindowSeconds`
  of its last sighting (sliding) is answered `"duplicate"` and not stored/logged/alerted.
- `Services/PlateAlertQueue.cs` + `PlateAlertHandler.cs` — the webhook enqueues each stored event (queue exists only
  with the bot on, so the endpoint takes it as `[FromServices] PlateAlertQueue?`); `TelegramBotService` drains it and
  sends the alert to every known user (`AccessService.ListUsersWithPermissionAsync`); only those allowed `open_gate`
  get the "Open gate"/"Deny" buttons. Callback data is `open:<guid N>`/`deny:<guid N>`.
  `DecideAsync` is Telegram-free (tested directly); first press wins via the `plate_decisions` PK, a failed relay
  removes the decision again. `plate_alert_messages` (key `ChatId`+`MessageId`) remembers every copy, including
  resends, so all of them get edited. Resend = `POST /api/access/events/{id}/resend-alert` (admin cookie, undecided
  events only) → same queue with `PlateAlert.ResentBy` set.
- `wwwroot/index.html` — single-file viewer, no external dependencies. Tabs: `Plate events`, `Bot commands` (`/#bot`)
  driven by a `views` map (endpoint, filters, row renderer, details handler, own paging); `Access` (`/#access`) is
  separate code (`loadAccess`/`refreshAccess`) because it's a signed-in admin screen, not a filtered list.
  `data-view` can list several tabs (`data-view="events bot"`).

## Access model

- owner = `TelegramBot:OwnerUserId` (config only, never a DB role, can't be demoted); admin = `bot_users.Role`;
  user = everyone else, with rows in `user_permissions`. Admins can use every command.
- Only the owner changes roles; admins grant/revoke for regular users only. Without an owner configured nobody is admin.
- `@username` resolves only for users who have messaged the bot (Telegram has no username lookup for bots).
- DataProtection keys persist to `dp-keys/` next to the DB (`/data/dp-keys` in Docker) so cookies survive restarts.

## Configuration

- Settings come from `appsettings.json`, overridden by env vars using `__` for nesting
  (`TelegramBot__BotToken` → `TelegramBot:BotToken`).
- `docker-compose.yml` maps short names from `.env` (`API_KEY`, `TELEGRAM_BOT_TOKEN`, `TELEGRAM_OWNER_ID`,
  `WEB_PUBLIC_URL`, `RELAY_BASE_URL`, ...) to those env vars. Add new settings in three places: `appsettings.json`
  default, compose `environment:` mapping, README table.
- Never bake secrets into the image (`ENV`/`COPY .env`) and never commit `.env`; it's in `.gitignore` and `.dockerignore`.

## Testing

- `AccessServiceTests` — rules against SQLite `:memory:`.
- `AccessApiTests` — real app via `WebApplicationFactory<Program>` on a temp DB; gets login codes straight from
  `LoginCodeStore` instead of the bot. Use this pattern for any new endpoint that needs a signed-in admin.

## Gotchas

- The entity is `BotCommandLog`, not `BotCommand`, because Telegram.Bot has its own `BotCommand` type.
- Telegram.Bot is v22: APIs are `SendMessage`/`SendChatAction`/`SetMyCommands` (no `Async` suffix) and
  `ReceiverOptions.DropPendingUpdates` (formerly `ThrowPendingUpdates`).
- Don't run a second copy of the app with the bot enabled and the same token: Telegram allows one long-poller per bot.
- `/api/events` and `/api/bot-commands` are still unauthenticated.
- Running locally writes `events.db` (and `dp-keys/`) to the project folder; in Docker it's `/data` on the `lpr-data` volume.
