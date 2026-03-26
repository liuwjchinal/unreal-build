@echo off
setlocal

set "ROOT=%~dp0"
set "FRONTEND_DIR=%ROOT%frontend"

echo Starting Unreal Local Build development services...
echo.

start "Unreal Local Build Backend" cmd /k cd /d "%ROOT%" ^&^& dotnet run --project .\backend\Backend.csproj --no-launch-profile
start "Unreal Local Build Frontend" cmd /k cd /d "%FRONTEND_DIR%" ^&^& npm run dev

echo Backend:  http://localhost:5080
echo Frontend: http://localhost:5173
echo.
echo Close the opened terminal windows to stop the services.

endlocal
