@echo off
setlocal

set "ROOT=%~dp0"
set "FRONTEND_DIR=%ROOT%frontend"
set "WWWROOT_DIR=%ROOT%backend\wwwroot"

echo Preparing Unreal Local Build production startup...
echo.

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

echo Starting backend on http://0.0.0.0:5080 ...
start "Unreal Local Build Backend" cmd /k cd /d "%ROOT%" ^&^& dotnet run --project .\backend\Backend.csproj --no-build --no-launch-profile

echo.
echo Backend: http://localhost:5080
echo LAN:     http://<your-ip>:5080
echo.
echo Close the opened terminal window to stop the service.
goto :eof

:fail
echo.
echo Startup failed. Check the error output above.
exit /b 1
