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
| `Logging__Console__FormatterName` | `json` | `json` writes one JSON object per line. `simple` gives plain-text lines. |
| `Logging__LogLevel__LprWebhook.RawBody` | `Debug` | Raw request bodies are logged at Debug. Set this to `Information` to stop logging them. |

For compose, put values in a `.env` file next to `docker-compose.yml`:

```env
API_KEY=change-me
SWAGGER_ENABLED=true
```

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

### `GET /health`
Returns `Healthy` if the app can reach the database. The container's `HEALTHCHECK` calls it through
`dotnet LprWebhook.Api.dll --healthcheck`, because the runtime image has no curl.

## Development

```
src/LprWebhook.Api/
  Program.cs                 startup: SQLite, health checks, OpenAPI/Swagger UI
  Contracts/                 request/response records
  Parsing/                   lenient JSON parsing and camera timestamp parsing
  Data/                      PlateEvent entity, EventsDbContext, EnsureCreated at startup
  Endpoints/EventEndpoints.cs
tests/LprWebhook.Api.Tests/  xUnit tests for deserialization and timestamps
```

Run the tests (no local SDK needed):

```sh
docker build --target test .
```

With the .NET 10 SDK installed you can run `dotnet test`, or start the app with `dotnet run --project src/LprWebhook.Api`.
Running locally writes `events.db` to the project folder.

The database schema is created with `EnsureCreated()` at startup. If you change the entity later, either switch to EF
migrations or delete the volume.
