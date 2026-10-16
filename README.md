# LPR Webhook API

A small ASP.NET Core (.NET 10) minimal API that receives `plate_recognized` webhooks from a license plate
recognition (LPR) system. It logs each event and stores it in SQLite so you can inspect it later.

## Установка на сервер (кратко, на русском)

API работает в Docker-контейнере. На сервере нужны только **Git** (чтобы скачать проект) и **Docker** (чтобы его
запустить); .NET ставить не нужно — готовый образ берётся из `ghcr.io/zaitolzhas/lpr-webhook-api`.

### 1. Установить Git и Docker

**Linux (Ubuntu/Debian):**

```sh
sudo apt update && sudo apt install -y git curl
curl -fsSL https://get.docker.com | sudo sh     # Docker Engine + плагин docker compose
sudo usermod -aG docker $USER                   # чтобы запускать docker без sudo; затем перелогиньтесь
docker --version && docker compose version      # проверка
```

**Windows (10/11 или Server с WSL2):**

```powershell
winget install --id Git.Git -e
winget install --id Docker.DockerDesktop -e
```

После установки перезагрузите компьютер и один раз запустите Docker Desktop (он попросит включить WSL2). В настройках
Docker Desktop включите *Start Docker Desktop when you sign in*, чтобы API поднимался после перезагрузки.

### 2. Скачать проект и запустить

**Windows, в один клик:** скачайте
[`deploy.bat`](https://raw.githubusercontent.com/zaitOlzhas/plate-recognition-api/main/deploy.bat) в любую папку
(например `C:\lpr`) и запустите двойным щелчком. Он скачает проект, при первом запуске создаст `.env` и откроет его в
Блокноте — заполните переменные (см. таблицу ниже), сохраните и закройте Блокнот. Дальше скрипт сам скачает образ,
запустит контейнер и откроет страницу событий. Для обновления просто запустите `deploy.bat` ещё раз.

**Linux (или Windows вручную):**

```sh
git clone https://github.com/zaitOlzhas/plate-recognition-api.git
cd plate-recognition-api
nano .env                                        # создайте файл с настройками (пример ниже)
docker compose pull && docker compose up -d --no-build
docker compose ps                                # через ~30 с статус должен стать "healthy"
```

Пример `.env` (файл лежит рядом с `docker-compose.yml`, в git его не коммитить):

```env
LPR_IMAGE=ghcr.io/zaitolzhas/lpr-webhook-api:latest
API_KEY=придумайте-длинный-ключ

TELEGRAM_BOT_ENABLED=true
TELEGRAM_BOT_TOKEN=1234567890:ABCdefGHIjklMNOpqrsTUVwxyz
TELEGRAM_OWNER_ID=123456789
WEB_PUBLIC_URL=http://192.168.1.50:5080
RELAY_BASE_URL=http://192.168.1.10
RELAY_ENDPOINT=out1_pulse
```

### 3. Проверить и подключить камеру

- Страница событий: `http://<IP-сервера>:5080/`, проверка здоровья: `http://<IP-сервера>:5080/health` → `Healthy`.
- В LPR-системе укажите адрес вебхука `http://<IP-сервера>:5080/api/events/plate` и, если задан `API_KEY`,
  заголовок `X-Api-Key: <значение API_KEY>`.
- Порт **5080** должен быть открыт в файрволе сервера (Linux: `sudo ufw allow 5080/tcp`).

Полезные команды (из папки проекта):

```sh
docker compose logs -f lpr-webhook                       # логи
git pull && docker compose pull && docker compose up -d --no-build   # обновление до последней версии
docker compose up -d --no-build                          # применить изменения .env (restart их не подхватит)
```

Данные (база `events.db`) хранятся в Docker-томе `lpr-data` и переживают обновления и перезапуски;
`docker compose down -v` их **удаляет**.

### Переменные окружения (`.env`)

Обязательных переменных нет: без `.env` API запустится и будет принимать события, но без ключа и без Telegram-бота.

| Переменная | По умолчанию | Что означает |
|---|---|---|
| `LPR_IMAGE` | `lpr-webhook-api:latest` | Какой Docker-образ запускать. Укажите `ghcr.io/zaitolzhas/lpr-webhook-api:latest`, чтобы скачивать готовый образ, а не собирать его на сервере. Тег `:sha-1a2b3c4` фиксирует конкретную версию (например, для отката). |
| `API_KEY` | *(пусто)* | Секретный ключ вебхука. Если задан, LPR-система должна присылать заголовок `X-Api-Key` с этим значением, иначе ответ 401. Пусто — принимаются все запросы. |
| `SWAGGER_ENABLED` | `true` | Показывать документацию API на `/swagger`. |
| `DUPLICATE_WINDOW_SECONDS` | `30` | Камера шлёт один и тот же номер много раз, пока машина в кадре. Повтор того же номера с той же камеры в течение этого числа секунд с последнего появления не сохраняется и не отправляется в Telegram. `0` — отключить. |
| `CORS_ALLOWED_ORIGINS` | *(пусто)* | Нужен только если открывать `index.html` прямо с диска (тогда `null`). Обычно не трогать. |
| `TELEGRAM_BOT_ENABLED` | `false` | Включить Telegram-бота (уведомления о номерах, кнопки «Открыть шлагбаум»/«Отклонить», команда `/open_gate`). |
| `TELEGRAM_BOT_TOKEN` | *(пусто)* | Токен бота от [@BotFather](https://t.me/botfather). Без него бот не запустится. |
| `TELEGRAM_OWNER_ID` | *(пусто)* | Числовой Telegram ID владельца — главного администратора. **Без него никто не сможет выполнять команды.** Узнать свой ID: запустите бота без этой переменной и отправьте ему `/help`. |
| `WEB_PUBLIC_URL` | *(пусто)* | Адрес, по которому браузер администратора открывает этот сервер, например `http://192.168.1.50:5080`. Нужен, чтобы команда `/weblogin` присылала ссылку для входа во вкладку Access; если пусто — бот пришлёт код, который нужно вставить вручную. |
| `RELAY_BASE_URL` | `http://192.168.1.10` | Адрес реле (контроллера шлагбаума/ворот) в локальной сети. |
| `RELAY_ENDPOINT` | `out1_pulse` | Путь на реле, на который отправляется `POST` при открытии ворот (`<RELAY_BASE_URL>/<RELAY_ENDPOINT>`). |

Подробности по каждой функции — в разделах ниже (на английском).

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

### Run the published image (no build on the server)

Every new commit on `main` (a push or a merged PR) is tested and published by GitHub Actions
(`.github/workflows/docker-publish.yml`) as `ghcr.io/zaitolzhas/lpr-webhook-api`, tagged `latest` and
`sha-<short commit>`. Pull requests only run the tests.

On the server you need just `docker-compose.yml` and a `.env` with your settings plus:

```env
LPR_IMAGE=ghcr.io/zaitolzhas/lpr-webhook-api:latest
```

```sh
docker compose pull && docker compose up -d --no-build   # also how you update to the newest image
```

Pin a version with `LPR_IMAGE=ghcr.io/zaitolzhas/lpr-webhook-api:sha-1a2b3c4` to stay on (or roll back to) a commit.

#### One-click install / update on Windows (`deploy.bat`)

Needs Git and Docker Desktop. Download
[`deploy.bat`](https://raw.githubusercontent.com/zaitOlzhas/plate-recognition-api/main/deploy.bat) into any folder
(e.g. `C:\lpr`) and double-click it. It clones the repo next to itself (or pulls the latest code), starts Docker
Desktop if needed, creates `.env` on the first run and opens it in Notepad, pulls the newest image (building locally
if the pull fails), restarts the container, waits until it is healthy and opens the viewer. Double-click it again to
update; `.env` and the data volume are kept. The copy inside the cloned folder works the same way.
The package must be **public** to pull without `docker login` (GitHub → Packages → `lpr-webhook-api` → Package
settings → Change visibility); new packages start private. The image is built for `linux/amd64`.

### Configuration (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `API_KEY` | *(empty)* | If set, `POST /api/events/plate` requires the header `X-Api-Key: <value>` (401 otherwise). If empty, all requests are accepted. |
| `ConnectionStrings__Default` | `Data Source=/data/events.db` | SQLite connection string. |
| `Swagger__Enabled` | `true` | Serves `/swagger` and `/openapi/v1.json`, in Production too. Compose reads it from `SWAGGER_ENABLED`. |
| `PlateEvents__DuplicateWindowSeconds` | `30` | The camera sends the same plate several times per second while a car is in view. A plate seen again by the same camera within this many seconds of its *last* sighting is dropped (see [`POST /api/events/plate`](#post-apieventsplate)). Every repeat restarts the window, so a car standing at the gate is one event. `0` turns this off. Compose reads it from `DUPLICATE_WINDOW_SECONDS`. |
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
3. Configure the environment variables (see below). Set `TELEGRAM_OWNER_ID` to your own Telegram user ID: if you don't
   know it, start the bot without it and send `/help`, which replies with your ID.
4. Start the application with `docker compose up -d --build`
5. Find your bot on Telegram and send `/open_gate`. Then give other people access (see [Access control](#access-control)).

#### Configuration

| Variable | Default | Meaning |
|---|---|---|
| `TelegramBot__Enabled` | `false` | Turns the bot on. Compose reads it from `TELEGRAM_BOT_ENABLED`. |
| `TelegramBot__BotToken` | *(empty)* | Bot token from @BotFather. If it's empty, the bot doesn't start and a warning is logged. Compose reads it from `TELEGRAM_BOT_TOKEN`. |
| `TelegramBot__OwnerUserId` | *(empty)* | Telegram user ID of the owner, the first admin. **Without it nobody can run commands.** Compose reads it from `TELEGRAM_OWNER_ID`. |
| `TelegramBot__WebBaseUrl` | *(empty)* | How admins' browsers reach this API, e.g. `http://192.168.1.50:5080`. `/weblogin` uses it to send a clickable link; if it's empty, the bot sends a code to paste into the Access tab instead. Compose reads it from `WEB_PUBLIC_URL`. |
| `TelegramBot__Relay__BaseUrl` | `http://192.168.1.10` | Base URL of the relay device. Compose reads it from `RELAY_BASE_URL`. |
| `TelegramBot__Relay__Endpoint` | `out1_pulse` | Path on the relay that `/open_gate` sends a `POST` to. Compose reads it from `RELAY_ENDPOINT`. |

For compose, add to your `.env` file (never commit it — it's in `.gitignore` and `.dockerignore`):

```env
TELEGRAM_BOT_ENABLED=true
TELEGRAM_BOT_TOKEN=1234567890:ABCdefGHIjklMNOpqrsTUVwxyz
TELEGRAM_OWNER_ID=123456789
WEB_PUBLIC_URL=http://192.168.1.50:5080
RELAY_BASE_URL=http://192.168.1.10
RELAY_ENDPOINT=out1_pulse
```

#### Commands

| Command | Who | What it does |
|---|---|---|
| `/help`, `/start` | everyone | Shows your Telegram ID, your role and the commands you can use |
| `/open_gate` | users granted `open_gate`, admins | Triggers the configured relay endpoint (e.g. opens a gate) |
| `/users` | admins | Lists known users with their roles and permissions |
| `/grant <@user\|id> <permission>` | admins | Allows a command, e.g. `/grant @bob open_gate` |
| `/revoke <@user\|id> <permission>` | admins | Removes it again |
| `/weblogin` | admins | Private chat only. Replies with a one-time sign-in link (or code) for the web Access tab |
| `/promote <@user\|id>` | admins | Makes someone an admin |
| `/demote <@user\|id>` | admins | Removes admin (not the owner) |

`/open_gate` replies with a success or error message based on the relay's response. Users who aren't allowed get
"Not allowed. Your Telegram ID is N — send it to an admin", and the attempt is logged with outcome `denied`.
Commands sent in a group as `/open_gate@YourBot` work the same way.

#### Plate alerts

Every plate event that is stored (repeats are not, see `PlateEvents__DuplicateWindowSeconds`) is sent as a message
to everyone the bot knows: the owner and every user who has messaged the bot or was added by ID. It goes to their
private chat with the bot, so they must have started the bot at least once. Everyone sees the event, but only people
who may use `/open_gate` (the owner, admins and users granted `open_gate`) get the buttons:

```
123ABC02                  ← bold
2026-10-05 14:23:07
Camera: Gate Entrance
[Открыть шлагбаум] [Отклонить]
```

The plate is the bold first line (Telegram has no font sizes, so bold is the most it can stand out). The time is the
camera time; if the camera didn't send a usable one, it's the time the API received the event, in UTC.

- **Открыть шлагбаум** (Open gate) pulses the relay like `/open_gate` and marks the event `granted`. If the relay fails, the event stays
  undecided and the buttons stay, so you can try again.
- **Отклонить** (Deny) only marks the event `denied`.
- The first press decides for everybody: every copy of the alert, including those sent without buttons, shows who decided
  ("✅ Gate opened by @bob" / "⛔ Denied by @bob"). A later press on a stale copy answers "Already granted by @bob".
- Someone whose `open_gate` permission was revoked after the alert went out gets "Not allowed".
- **Resend:** a signed-in admin can click **Resend** on any plate event row in the viewer (or **Resend Telegram alert**
  in its details). Everyone gets another message for the same event, ending in "Resent by @admin". The earlier copies
  keep working, and whichever button is pressed first decides and updates every copy. Resending an event that was
  already decided reopens it (after a confirmation): the decision is cleared so the new buttons can decide again.
  The earlier press is still in the Bot commands log.

The decision is stored in the `plate_decisions` table and shown in the viewer's Decision column and in
`GET /api/events`. Button presses are logged in the command log like typed commands (`command` `/open_gate` or
`/deny`, `text` `[button] open:<event id>`).

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
- `outcome`: `success`, `failed` (the relay said no or was unreachable, the command had bad arguments, or the alert was already decided), `error`
  (unexpected exception), `denied` (the user isn't allowed to run it) or `ignored` (plain text or an unknown command)
- `resultMessage`, `relayStatusCode`, `durationMs`

If saving a row fails, the error is logged and the bot carries on.

#### Access control

Nobody can run a command unless they've been given it.

| Role | Who | Can |
|---|---|---|
| owner | `TELEGRAM_OWNER_ID`, from config only | Everything. Can't be demoted. |
| admin | promoted by the owner or another admin | Every command. Grants and revokes commands for regular users. Promotes and demotes admins. Signs in to the web Access tab. |
| user | anyone who messaged the bot, or was added by ID | Only the commands granted to them |

Any admin can promote or demote anyone except the owner. Every check reads the database, so a revoke or demotion takes
effect immediately, including for an open web session.

**Finding people:** `@username` only works for people who have messaged the bot at least once, because Telegram doesn't
let bots look up usernames. Otherwise ask them to send `/help` to the bot, which tells them their numeric ID, and
use that.

**Audit trail:** every grant, revoke, promote, demote and add-user is stored in the `permission_audit` table. Refused
attempts are stored too. Each row has who did it, to whom, what, when, whether it came from `telegram` or `web`, and
the result (`ok`, `denied` or `invalid`).

**Web Access tab:** open `http://<host>:5080/#access`.
1. Send `/weblogin` to the bot in a private chat.
2. Open the link it sends, or paste the code into the tab. Each link or code works once and expires after 5 minutes.
3. You're signed in with a cookie for 12 hours (sliding).

The tab lists users with a checkbox per command, has Make admin / Demote buttons, lets you add a user by
Telegram ID, and shows the audit trail. The cookie's encryption keys are kept in `dp-keys/` next to the database (in
Docker: the `lpr-data` volume), so sign-ins survive restarts.

The read-only endpoints `/api/events` and `/api/bot-commands` still need no sign-in; see the API section.

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
external dependencies. It has three tabs: **Plate events**, **Bot commands** (`/#bot`) and **Access** (`/#access`).
The first two are read-only and share the API, date range, page size and auto-refresh; each keeps its own filters and
page. The Access tab is for admins only (see [Access control](#access-control)) and only works when the page is opened
from the API, not from disk.

- **Table:** received time (in your local time), camera time, plate, direction, list, camera, server and decision
  ("✓ Granted by @bob" / "✕ Denied by @bob" from the [Telegram alert](#plate-alerts), or "No decision"), newest first.
- **Filters:** plate (matches any part), camera ID, received from/to, and page size, with pagination.
- **Refresh:** a Refresh button, plus auto-refresh every 3–60 s (on by default). Auto-refresh pauses while the tab is
  hidden or a detail view is open, and refreshes as soon as you come back. New events are briefly highlighted.
- **Details:** click a row to see every field and the raw JSON exactly as received, with a Copy button. Every
  plate event has a **Resend** button in its row and a **Resend Telegram alert** button in its details (admins signed
  in on the Access tab only).
- **Bot commands tab:** received time, user, chat ID, message, outcome (colored), relay result and duration, filterable
  by user, command and outcome. Click a row to see every stored field and the full message text.

You can also open the file straight from disk. In that case set `CORS_ALLOWED_ORIGINS=null`, because otherwise the
browser blocks the requests. The page has an API field for when the service isn't on `localhost:5080`.

## API

### `POST /api/events/plate`
- `200 {"status":"ok","id":"<guid>"}`: the event was stored (and a [Telegram alert](#plate-alerts) is sent if the bot is on).
- `200 {"status":"duplicate","id":"<guid of the first event>"}`: the same plate from the same camera was already
  received within `PlateEvents__DuplicateWindowSeconds`. Nothing is stored, logged or sent. Plates are compared
  ignoring case, spaces and dashes; the camera is `camera.id`, or `camera.name` if there's no id.
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

Every request's raw body, except duplicates, is logged at Debug (category `LprWebhook.RawBody`). Rejected and patched bodies are also logged
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
- Each item has `decision` (`granted`, `denied` or `null` while undecided) and `decidedBy`.

### `GET /api/events/{id}`
Returns every stored field plus `rawJson` and `decision`/`decidedBy`/`decidedAtUtc`, or 404 if the id doesn't exist.

### `GET /api/bot-commands?user=&chatId=&command=&outcome=&from=&to=&page=&pageSize=`
Returns messages received by the Telegram bot, newest first, as `{ page, pageSize, total, items }`.
- `user` is a numeric Telegram user ID (exact match), or text matched against any part of the username or full name.
  A leading `@` is ignored.
- `command` is the command with or without the leading `/` (for example `open_gate`).
- `outcome` is one of `success`, `failed`, `error`, `denied` or `ignored`.
- `from`, `to` and `pageSize` work the same way as on `/api/events`.

### Access (admins only)

Sign-in: `GET /login?code=` (the `/weblogin` link: sets the `lpr_admin` cookie and redirects to `/#access`),
`POST /api/auth/login {"code":"..."}`, `GET /api/auth/me`, `POST /api/auth/logout`.

Everything under `/api/access` needs that cookie, and the user must still be an admin; otherwise it returns 401 or 403.

| Endpoint | Does |
|---|---|
| `GET /api/access/permissions` | Commands that can be granted |
| `GET /api/access/users` | Users with role and permissions |
| `POST /api/access/users {"userId":123}` | Add a user by Telegram ID |
| `PUT` / `DELETE /api/access/users/{id}/permissions/{permission}` | Grant / revoke |
| `PUT /api/access/users/{id}/role {"role":"admin"\|"user"}` | Promote / demote (admins; not the owner) |
| `GET /api/access/audit?page=&pageSize=` | Permission changes, newest first |
| `POST /api/access/events/{id}/resend-alert` | Send a plate event's Telegram alert again. A decided event is reopened (its decision is cleared). `400` if the bot is off, `404` if it doesn't exist |

Changes return `200 {"status":"ok","message":"..."}`, `403` with `"denied"` if you aren't allowed, or `400` with
`"invalid"` for bad input (e.g. an unknown permission).

### `GET /health`
Returns `Healthy` if the app can reach the database. The container's `HEALTHCHECK` calls it through
`dotnet LprWebhook.Api.dll --healthcheck`, because the runtime image has no curl.

## Development

```
src/LprWebhook.Api/
  Program.cs                 startup: SQLite, health checks, OpenAPI/Swagger UI, Telegram bot
  Contracts/                 request/response records
  Parsing/                   lenient JSON parsing and camera timestamp parsing
  Data/                      entities (plate events, bot commands, users/permissions/audit), EventsDbContext, schema setup
  Endpoints/                 events, bot commands, auth (/weblogin sign-in) and access (admin API)
  Access/                    AccessService (all role/permission rules + audit), BotPermissions catalog, LoginCodeStore
  Configuration/             TelegramBotOptions (bound from the "TelegramBot" section)
  Services/                  TelegramBotService (long polling, logging), BotCommandHandler (one method per command),
                             PlateAlertHandler (alert + Open gate/Deny buttons), PlateDeduplicator,
                             RelayService (POST to the relay)
tests/LprWebhook.Api.Tests/  xUnit tests: parsing, timestamps, AccessService rules, sign-in + admin API end to end
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
