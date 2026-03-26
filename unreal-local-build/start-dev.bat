@echo off
setlocal

set "ROOT=%~dp0"
set "FRONTEND_DIR=%ROOT%frontend"
set "VITE_CMD=%FRONTEND_DIR%\node_modules\.bin\vite.cmd"
set "BACKEND_URL=http://localhost:5080/api/health"

echo Starting Unreal Local Build development services...
echo.

if not exist "%FRONTEND_DIR%\node_modules" (
    echo Frontend dependencies not found. Running npm install...
    call npm.cmd --prefix "%FRONTEND_DIR%" install
    if errorlevel 1 goto :fail
)

if not exist "%VITE_CMD%" (
    echo Vite executable not found. Running npm install...
    call npm.cmd --prefix "%FRONTEND_DIR%" install
    if errorlevel 1 goto :fail
)

call :is_port_listening 5080
if errorlevel 1 (
    echo Backend is not running. Starting backend...
    start "Unreal Local Build Backend" cmd /k cd /d "%ROOT%" ^&^& dotnet run --project .\backend\Backend.csproj --no-launch-profile
    call :wait_for_backend
    if errorlevel 1 goto :fail
) else (
    echo Backend is already listening on port 5080.
)

call :is_port_listening 5173
if errorlevel 1 (
    echo Frontend is not running. Starting frontend...
    start "Unreal Local Build Frontend" cmd /k cd /d "%FRONTEND_DIR%" ^&^& npm.cmd run dev
) else (
    echo Frontend is already listening on port 5173.
)

echo Backend:  http://localhost:5080
echo Frontend: http://localhost:5173
echo.
echo Close the opened terminal windows to stop the services.
goto :eof

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
if %WAIT_COUNT% GEQ 20 (
    echo Backend did not become healthy in time.
    exit /b 1
)
timeout /t 1 /nobreak >nul
goto wait_loop

:fail
echo.
echo Startup failed. Check the error output above.
exit /b 1

endlocal
