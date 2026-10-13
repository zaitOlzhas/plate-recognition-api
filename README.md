# LPR Webhook API

A small ASP.NET Core (.NET 10) minimal API that receives `plate_recognized` webhooks from a license plate
recognition (LPR) system. It logs each event and stores it in SQLite so you can inspect it later.

## Run

```sh
docker compose up -d --build
```

| What | URL |
|---|---|
| **Webhook URL to configure in the LPR system** | `http://<host>:5080/api/events/plate` |
| **Events viewer (web page)** | `http://<host>:5080/` |
| List events | `http://<host>:5080/api/events` |
| Swagger UI | `http://<host>:5080/swagger` |
| Health | `http://<host>:5080/health` |

Data is stored in `/data/events.db` inside the named volume `lpr-data`, so it survives restarts and rebuilds.
`docker compose down -v` deletes it.

### Configuration (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `API_KEY` | *(empty)* | If set, `POST /api/events/plate` requires the header `X-Api-Key: <value>` (401 otherwise). If empty, all requests are accepted. |
| `ConnectionStrings__Default` | `Data Source=/data/events.db` | SQLite connection string. |
| `Swagger__Enabled` | `true` | Serves `/swagger` and `/openapi/v1.json`, in Production too. Compose reads it from `SWAGGER_ENABLED`. |
| `Cors__AllowedOrigins` | *(empty)* | Comma-separated origins allowed to read the API from a browser (GET only). Use `null` to open `index.html` straight from disk. Compose reads it from `CORS_ALLOWED_ORIGINS`. |
| `Logging__Console__FormatterName` | `json` | `json` writes one JSON object per line. `simple` gives plain-text lines. |
| `Logging__LogLevel__LprWebhook.RawBody` | `Debug` | Raw request bodies are logged at Debug. Set this to `Information` to stop logging them. |

For compose, put values in a `.env` file next to `docker-compose.yml`:

```env
API_KEY=change-me
SWAGGER_ENABLED=true
```

### Telegram Bot

The application includes an optional Telegram bot that can trigger a relay device (for example, to open a gate).

#### Setup

1. Create a bot with [@BotFather](https://t.me/botfather) on Telegram
2. Copy the bot token provided by BotFather
3. Configure the environment variables (see below)
4. Start the application with `docker compose up -d --build`
5. Find your bot on Telegram and send `/open_gate`

#### Configuration

| Variable | Default | Meaning |
|---|---|---|
| `TelegramBot__Enabled` | `false` | Turns the bot on. Compose reads it from `TELEGRAM_BOT_ENABLED`. |
| `TelegramBot__BotToken` | *(empty)* | Bot token from @BotFather. If it's empty, the bot doesn't start and a warning is logged. Compose reads it from `TELEGRAM_BOT_TOKEN`. |
| `TelegramBot__Relay__BaseUrl` | `http://192.168.1.10` | Base URL of the relay device. Compose reads it from `RELAY_BASE_URL`. |
| `TelegramBot__Relay__Endpoint` | `out1_pulse` | Path on the relay that `/open_gate` sends a `POST` to. Compose reads it from `RELAY_ENDPOINT`. |

For compose, add to your `.env` file (never commit it — it's in `.gitignore` and `.dockerignore`):

```env
TELEGRAM_BOT_ENABLED=true
TELEGRAM_BOT_TOKEN=1234567890:ABCdefGHIjklMNOpqrsTUVwxyz
RELAY_BASE_URL=http://192.168.1.10
RELAY_ENDPOINT=out1_pulse
```

#### Commands

- `/open_gate` - Triggers the configured relay endpoint (e.g., opens a gate)

The bot will respond with a success or error message based on the relay's response.

#### Expected Relay Response

The relay endpoint should respond with HTTP 200 and JSON body:

```json
{
  "status": "ok",
  "message": "out1_pulse"
}
```

If the relay returns this response, the bot will confirm success to the user. Any other response or error will result in an error message.

#### Command log

Every text message the bot receives is stored in the `bot_commands` table and logged at Information
(`Bot command <id>: user=... command=... outcome=...`). This includes plain text and unknown commands. Each row has:

- `receivedAtUtc` (when the API got the message) and `sentAtUtc` (when Telegram says the user sent it)
- `chatId`, `chatType`, `userId`, `username`, `userFullName`, `messageId`
- `text` (the full message) and `command` (lower-cased, without `@botname`, e.g. `/open_gate`; `null` for plain text)
- `outcome`: `success`, `failed` (the relay said no or was unreachable), `error` (unexpected exception) or `ignored`
  (not a known command)
- `resultMessage`, `relayStatusCode`, `durationMs`

If saving a row fails, the error is logged and the bot carries on.

#### Access Control

Currently, anyone who knows the bot can trigger the relay. For production use, consider restricting access by adding user ID whitelisting in a future enhancement.

## Send a test event

```sh
curl -X POST http://localhost:5080/api/events/plate \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: change-me" \
  -d '{
    "event": "plate_recognized",
    "timestamp": "2026-10-05T14:23:07.512",
    "date": { "year": "2026", "month": "10", "day": "05", "hour": "14", "minute": "23", "second": "07", "millisecond": "512" },
    "server": { "name": "LPR-Server-1", "domain_name": "lpr.local" },
    "camera": { "name": "Gate Entrance", "number": 1, "index": 0, "id": "cam-001" },
    "plate": { "text": "123ABC02", "direction": "in", "description": "", "list": { "name": "Whitelist", "description": "Employees" } }
  }'
# -> {"status":"ok","id":"01a109bb-8fc6-7e94-9813-312097efadae"}
```

You can also run `./send-sample.sh [base-url]`, which posts `sample-payload.json`. It sends `X-Api-Key` if `API_KEY` is set
in your shell. `test-request.http` has ready-made requests for VS Code REST Client and Rider/Visual Studio.

Inspect what was received:

```sh
curl "http://localhost:5080/api/events?plate=123ABC&cameraId=cam-001&page=1&pageSize=20"
curl "http://localhost:5080/api/events/<id>"     # includes rawJson, the exact body the vendor sent
docker compose logs -f lpr-webhook
```

## Events viewer

Open `http://<host>:5080/` in a browser. The page is `src/LprWebhook.Api/wwwroot/index.html`, a single file with no
external dependencies, and it is read-only. It has two tabs: **Plate events** and **Bot commands** (`/#bot` opens the
second one directly). The API, date range, page size and auto-refresh are shared; each tab keeps its own filters and page.

- **Table:** received time (in your local time), camera time, plate, direction, list, camera and server, newest first.
- **Filters:** plate (matches any part), camera ID, received from/to, and page size, with pagination.
- **Refresh:** a Refresh button, plus auto-refresh every 3–60 s (on by default). Auto-refresh pauses while the tab is
  hidden or a detail view is open, and refreshes as soon as you come back. New events are briefly highlighted.
- **Details:** click a row to see every field and the raw JSON exactly as received, with a Copy button.
- **Bot commands tab:** received time, user, chat ID, message, outcome (colored), relay result and duration, filterable
  by user, command and outcome. Click a row to see every stored field and the full message text.

You can also open the file straight from disk. In that case set `CORS_ALLOWED_ORIGINS=null`, because otherwise the
browser blocks the requests. The page has an API field for when the service isn't on `localhost:5080`.

## API

### `POST /api/events/plate`
- `200 {"status":"ok","id":"<guid>"}`: the event was stored.
- `400 {"status":"error","error":"..."}`: the body is empty or not valid JSON, or `plate.text` is missing.
- `401`: `API_KEY` is set and the `X-Api-Key` header is missing or wrong.

Parsing is lenient, so differences in the vendor's format are absorbed instead of being rejected:
- JSON property names are snake_case (`domain_name`). Unknown fields are ignored.
- `camera.number` and `camera.index` may be numbers, numeric strings, `""`, `null` or junk. Anything unparseable becomes `null`.
  The template puts these placeholders without quotes (`"number": ${camera_number}`), so an empty value produces
  `"number": ,`, which is invalid JSON. The API patches such empty values to `null`, stores the event and logs a warning.
- `date.*` values may be strings or numbers.
- The Content-Type is not enforced, because some LPR systems send `text/plain`.
- **Camera time**: `timestamp` is parsed as the camera's local wall-clock time, with no timezone. Month, day and time parts
  may be 1 or 2 digits. Because the template is `.${millisecond}`, a 1–3 digit fraction is read as a millisecond count:
  `.5` is 5 ms and `.51` is 51 ms. Longer fractions are read as normal decimal fractions. If `timestamp` can't be parsed,
  the time is built from the `date` object instead.
- `receivedAtUtc` is the time this server received the event, in UTC.

Every request's raw body is logged at Debug (category `LprWebhook.RawBody`). Rejected and patched bodies are also logged
at Warning. Each stored event produces one Information line, for example:

```
Plate event 01a1…: plate=123ABC02 camera=Gate Entrance (cam-001) direction=in list=Whitelist cameraTime=2026-10-05T14:23:07.512
```

### `GET /api/events?plate=&cameraId=&from=&to=&page=&pageSize=`
Returns events newest first as `{ page, pageSize, total, items }`.
- `plate` matches any part of the plate text.
- `cameraId` must match exactly.
- `from` and `to` are ISO-8601 times compared with `receivedAtUtc`. A value with no offset is treated as UTC.
- `pageSize` defaults to 50, with a maximum of 500.

### `GET /api/events/{id}`
Returns every stored field plus `rawJson`, or 404 if the id doesn't exist.

### `GET /api/bot-commands?user=&chatId=&command=&outcome=&from=&to=&page=&pageSize=`
Returns messages received by the Telegram bot, newest first, as `{ page, pageSize, total, items }`.
- `user` is a numeric Telegram user ID (exact match), or text matched against any part of the username or full name.
  A leading `@` is ignored.
- `command` is the command with or without the leading `/` (for example `open_gate`).
- `outcome` is one of `success`, `failed`, `error` or `ignored`.
- `from`, `to` and `pageSize` work the same way as on `/api/events`.

### `GET /health`
Returns `Healthy` if the app can reach the database. The container's `HEALTHCHECK` calls it through
`dotnet LprWebhook.Api.dll --healthcheck`, because the runtime image has no curl.

## Development

```
src/LprWebhook.Api/
  Program.cs                 startup: SQLite, health checks, OpenAPI/Swagger UI, Telegram bot
  Contracts/                 request/response records
  Parsing/                   lenient JSON parsing and camera timestamp parsing
  Data/                      PlateEvent and BotCommandLog entities, EventsDbContext, schema setup at startup
  Endpoints/                 EventEndpoints.cs, BotCommandEndpoints.cs
  Configuration/             TelegramBotOptions (bound from the "TelegramBot" section)
  Services/                  TelegramBotService (long polling, /open_gate) and RelayService (POST to the relay)
tests/LprWebhook.Api.Tests/  xUnit tests for deserialization and timestamps
```

Run the tests (no local SDK needed):

```sh
docker build --target test .
```

With the .NET 10 SDK installed you can run `dotnet test`, or start the app with `dotnet run --project src/LprWebhook.Api`.
Running locally writes `events.db` to the project folder.

At startup the app runs EF's create script with `IF NOT EXISTS`, so new tables and indexes are added to an existing
database. Columns are not: if you add a property to an existing entity, either switch to EF migrations or delete the
volume.
