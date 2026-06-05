@echo off
setlocal

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "FRONTEND_DIR=%ROOT%\frontend"
set "BACKEND_DIR=%ROOT%\backend"
set "WWWROOT_DIR=%ROOT%\backend\wwwroot"
if not defined LOCAL_BUILD_ENV_ROOT set "LOCAL_BUILD_ENV_ROOT=F:\LocalBuildEnv"
set "ANDROID_SHELL_SCRIPT=%LOCAL_BUILD_ENV_ROOT%\scripts\Open-AndroidShell.ps1"
set "BACKEND_PORT=5080"
if not defined LOCAL_BUILD_BACKEND_HEALTH_TIMEOUT_SECONDS set "LOCAL_BUILD_BACKEND_HEALTH_TIMEOUT_SECONDS=60"
if not defined LOCAL_BUILD_BACKEND_ORIGIN set "LOCAL_BUILD_BACKEND_ORIGIN=http://localhost:%BACKEND_PORT%"
if not defined APP__SERVERURL set "APP__SERVERURL=http://0.0.0.0:%BACKEND_PORT%"
if not defined APP__FRONTENDDEVORIGIN set "APP__FRONTENDDEVORIGIN=http://localhost:5173"
set "BACKEND_URL=%LOCAL_BUILD_BACKEND_ORIGIN%/api/health"

echo Preparing Unreal Local Build production startup...
echo.

if not exist "%ANDROID_SHELL_SCRIPT%" (
    echo Missing Android shell script: %ANDROID_SHELL_SCRIPT%
    goto :fail
)

if not exist "%FRONTEND_DIR%\node_modules" (
    echo Frontend dependencies not found. Running npm install...
    call npm.cmd --prefix "%FRONTEND_DIR%" install
    if errorlevel 1 goto :fail
)

if not exist "%WWWROOT_DIR%\index.html" (
    echo Frontend static files not found. Running npm run build...
    call npm.cmd --prefix "%FRONTEND_DIR%" run build
    if errorlevel 1 goto :fail
)

call :is_port_listening %BACKEND_PORT%
if errorlevel 1 (
    echo Starting backend on %APP__SERVERURL% ...
    start "Unreal Local Build Backend" powershell.exe -ExecutionPolicy Bypass -File "%ANDROID_SHELL_SCRIPT%" -Profile Unreal -NoNewWindow -WorkingDirectory "%BACKEND_DIR%" -WindowTitle "Unreal Local Build Backend" -Command "dotnet run --project .\Backend.csproj --no-build --no-launch-profile"
    call :wait_for_backend
    if errorlevel 1 goto :fail
) else (
    echo Backend is already listening on port %BACKEND_PORT%.
)

echo.
echo Backend: %LOCAL_BUILD_BACKEND_ORIGIN%
echo LAN:     http://<your-ip>:%BACKEND_PORT%
echo.
echo Close the opened terminal window to stop the service.
exit /b 0

:is_port_listening
netstat -ano | findstr LISTENING | findstr ":%~1" >nul
if errorlevel 1 (
    exit /b 1
)
exit /b 0

:wait_for_backend
set /a WAIT_COUNT=0
:wait_loop
powershell -NoProfile -Command "try { $response = Invoke-WebRequest -UseBasicParsing -Uri '%BACKEND_URL%' -TimeoutSec 2; if ($response.StatusCode -eq 200) { exit 0 } else { exit 1 } } catch { exit 1 }"
if not errorlevel 1 exit /b 0
set /a WAIT_COUNT+=1
if %WAIT_COUNT% GEQ %LOCAL_BUILD_BACKEND_HEALTH_TIMEOUT_SECONDS% (
    echo Backend did not become healthy in %LOCAL_BUILD_BACKEND_HEALTH_TIMEOUT_SECONDS%s.
    exit /b 1
)
timeout /t 1 /nobreak >nul
goto wait_loop

:fail
echo.
echo Startup failed. Check the error output above.
exit /b 1
