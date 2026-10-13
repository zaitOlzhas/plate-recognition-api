# CLAUDE.md

ASP.NET Core (.NET 10) minimal API that receives `plate_recognized` webhooks from an LPR system, stores them in
SQLite, and serves a read-only events viewer. An optional Telegram bot can pulse a relay (gate) via `/open_gate`.
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
- `Endpoints/EventEndpoints.cs` — `POST /api/events/plate`, `GET /api/events`, `GET /api/events/{id}`.
- `Parsing/` — deliberately lenient JSON/timestamp parsing; vendor payloads can be malformed (e.g. `"number": ,`).
  Don't make it stricter without tests covering the vendor quirks listed in the README.
- `Data/` — `PlateEvent`, `EventsDbContext`. Schema is created with `EnsureCreated()`, no migrations.
- `Configuration/TelegramBotOptions.cs` — bound from the `TelegramBot` config section.
- `Services/` — `TelegramBotService` (hosted service, long polling) and `RelayService` (HTTP POST to the relay).
  Both are registered only when `TelegramBot:Enabled` is true.
- `wwwroot/index.html` — single-file events viewer, no external dependencies.

## Configuration

- Settings come from `appsettings.json`, overridden by env vars using `__` for nesting
  (`TelegramBot__BotToken` → `TelegramBot:BotToken`).
- `docker-compose.yml` maps short names from `.env` (`API_KEY`, `TELEGRAM_BOT_TOKEN`, `RELAY_BASE_URL`, ...) to those
  env vars. Add new settings in three places: `appsettings.json` default, compose `environment:` mapping, README table.
- Never bake secrets into the image (`ENV`/`COPY .env`) and never commit `.env`; it's in `.gitignore` and `.dockerignore`.

## Gotchas

- Telegram.Bot is v22: APIs are `SendMessage`/`SendChatAction` (no `Async` suffix) and
  `ReceiverOptions.DropPendingUpdates` (formerly `ThrowPendingUpdates`).
- The bot has no access control yet — anyone who finds it can trigger the relay.
- Running locally writes `events.db` to the project folder; in Docker it's `/data/events.db` on the `lpr-data` volume.
