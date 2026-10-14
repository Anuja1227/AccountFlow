@echo off
rem Starts AccountFlow (API + frontend) and a public preview link (Cloudflare quick tunnel).
rem The link is printed in this window and also written to tools\logs\preview.log.
rem Close this window to stop the link.
setlocal
cd /d "%~dp0.."
if not exist tools\logs mkdir tools\logs

rem 1) cloudflared: install once with winget if missing.
set "CF="
where cloudflared >nul 2>nul && set "CF=cloudflared"
if not defined CF if exist "%ProgramFiles(x86)%\cloudflared\cloudflared.exe" set "CF=%ProgramFiles(x86)%\cloudflared\cloudflared.exe"
if not defined CF if exist "%ProgramFiles%\cloudflared\cloudflared.exe" set "CF=%ProgramFiles%\cloudflared\cloudflared.exe"
if not defined CF (
    echo Installing cloudflared with winget...
    winget install --id Cloudflare.cloudflared -e --accept-source-agreements --accept-package-agreements
    if exist "%ProgramFiles(x86)%\cloudflared\cloudflared.exe" set "CF=%ProgramFiles(x86)%\cloudflared\cloudflared.exe"
    if exist "%ProgramFiles%\cloudflared\cloudflared.exe" set "CF=%ProgramFiles%\cloudflared\cloudflared.exe"
)
if not defined CF (
    echo cloudflared was not found after installing. Close this window, open a new one and run tools\preview.cmd again.
    pause
    exit /b 1
)

rem 2) API and frontend, each in its own window (skip if already running).
netstat -ano | findstr /r /c:":7242 .*LISTENING" >nul || start "AccountFlow API" cmd /k "cd /d Backend\AccountOpeningTracker.Api && dotnet run --launch-profile https"
netstat -ano | findstr /r /c:":5173 .*LISTENING" >nul || start "AccountFlow Frontend" cmd /k "cd /d Frontend && npm run dev"

echo Waiting for the app to start...
:wait
timeout /t 3 >nul
netstat -ano | findstr /r /c:":5173 .*LISTENING" >nul || goto wait
netstat -ano | findstr /r /c:":7242 .*LISTENING" >nul || goto wait

rem 3) Public link.
del tools\logs\preview.log 2>nul
echo.
echo Starting the preview link. Look for https://....trycloudflare.com below.
echo.
"%CF%" tunnel --url http://localhost:5173 --logfile tools\logs\preview.log
