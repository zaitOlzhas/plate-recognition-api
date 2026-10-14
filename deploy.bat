@echo off
rem ---------------------------------------------------------------------------
rem  LPR Webhook - install or update with one click (Windows, needs Git + Docker Desktop).
rem
rem  Put this file in any folder and double-click it. It:
rem    1. clones the repo next to itself (first run) or pulls the latest code;
rem    2. starts Docker Desktop if it isn't running;
rem    3. creates .env on the first run and opens it in Notepad for your settings;
rem    4. pulls the newest image from ghcr.io (builds locally if that fails);
rem    5. (re)starts the container, waits until it is healthy and opens the viewer.
rem  Run it again any time to update. Your data (Docker volume lpr-data) and .env are kept.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions

set "REPO_URL=https://github.com/zaitOlzhas/plate-recognition-api.git"
set "IMAGE=ghcr.io/zaitolzhas/lpr-webhook-api:latest"
set "PORT=5080"

rem "git pull" may replace this very file, and cmd reads a running batch file line by line. So run from a copy in
rem %TEMP% and tell it where the original lives.
if /i not "%~1"=="--from-temp" (
    copy /y "%~f0" "%TEMP%\lpr-deploy.bat" >nul
    "%TEMP%\lpr-deploy.bat" --from-temp "%~dp0."
    exit /b
)
rem No trailing backslash: git.exe would read \" at the end of a quoted path as a literal quote.
for %%I in ("%~2") do set "HERE=%%~fI"

rem Inside a clone (this file is in the repo root) update that clone; otherwise use a clone next to this file.
if exist "%HERE%\docker-compose.yml" (
    set "REPO=%HERE%"
) else (
    set "REPO=%HERE%\plate-recognition-api"
)

title LPR Webhook - deploy
echo.
echo === LPR Webhook deploy ===
echo Folder: %REPO%
echo.

where git >nul 2>&1 || (echo [ERROR] Git is not installed or not on PATH. & goto :fail)
where docker >nul 2>&1 || (echo [ERROR] Docker is not installed or not on PATH. & goto :fail)

rem ---- 1. code ----
if exist "%REPO%\.git" (
    echo [1/5] Getting the latest code...
    git -C "%REPO%" pull --ff-only
    if errorlevel 1 echo [WARN] Could not update the code ^(offline or local changes^). Continuing with the current version.
) else (
    echo [1/5] Downloading the code...
    git clone "%REPO_URL%" "%REPO%"
    if errorlevel 1 (echo [ERROR] git clone failed. & goto :fail)
)
cd /d "%REPO%" || goto :fail

rem ---- 2. Docker ----
echo [2/5] Checking Docker...
docker info >nul 2>&1
if errorlevel 1 (
    if exist "%ProgramFiles%\Docker\Docker\Docker Desktop.exe" (
        echo       Starting Docker Desktop, this can take a minute...
        start "" "%ProgramFiles%\Docker\Docker\Docker Desktop.exe"
    )
    for /l %%i in (1,1,60) do (
        docker info >nul 2>&1 && goto :docker_ok
        timeout /t 3 /nobreak >nul
    )
    echo [ERROR] Docker is not running. Start Docker Desktop and run this file again.
    goto :fail
)
:docker_ok

rem ---- 3. settings ----
if not exist ".env" (
    echo [3/5] First run: creating .env - fill it in, save and close Notepad to continue.
    (
        echo # LPR Webhook settings. Run deploy.bat again after changing them.
        echo LPR_IMAGE=%IMAGE%
        echo.
        echo # Webhook key the LPR system must send as X-Api-Key. Empty = no key needed.
        echo API_KEY=
        echo.
        echo # Telegram bot: token from @BotFather, your Telegram user ID ^(send /help to the bot^).
        echo TELEGRAM_BOT_ENABLED=false
        echo TELEGRAM_BOT_TOKEN=
        echo TELEGRAM_OWNER_ID=
        echo # How admins' browsers reach this server, e.g. http://192.168.1.50:%PORT%
        echo WEB_PUBLIC_URL=
        echo RELAY_BASE_URL=http://192.168.1.10
        echo RELAY_ENDPOINT=out1_pulse
    ) > ".env"
    start /wait notepad ".env"
) else (
    echo [3/5] Using existing .env
)
rem Older .env files may not name the image; without it compose would build locally instead of pulling.
findstr /b /c:"LPR_IMAGE=" ".env" >nul 2>&1 || set "LPR_IMAGE=%IMAGE%"

rem ---- 4. image ----
echo [4/5] Getting the latest image...
docker compose pull
if errorlevel 1 (
    echo [WARN] Could not pull the image ^(not published yet, private, or offline^). Building it here instead...
    docker compose build
    if errorlevel 1 (echo [ERROR] Build failed. & goto :fail)
)

rem ---- 5. start ----
echo [5/5] Starting the container...
docker compose up -d --no-build --remove-orphans
if errorlevel 1 (echo [ERROR] Could not start the container. & goto :fail)

echo       Waiting for it to become healthy...
for /l %%i in (1,1,40) do (
    for /f "delims=" %%s in ('docker inspect -f "{{.State.Health.Status}}" lpr-webhook 2^>nul') do (
        if "%%s"=="healthy" goto :healthy
    )
    timeout /t 3 /nobreak >nul
)
echo [WARN] Not healthy after 2 minutes. Recent logs:
docker compose logs --tail 30 lpr-webhook
goto :fail

:healthy
docker image prune -f >nul 2>&1
echo.
echo === Running: http://localhost:%PORT%/ ===
start "" "http://localhost:%PORT%/"
echo.
pause
exit /b 0

:fail
echo.
echo Something went wrong - see the messages above.
pause
exit /b 1
