@echo off
setlocal

set "ROOT=%~dp0"
set "FRONTEND_DIR=%ROOT%frontend"
set "VITE_CMD=%FRONTEND_DIR%\node_modules\.bin\vite.cmd"

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

start "Unreal Local Build Backend" cmd /k cd /d "%ROOT%" ^&^& dotnet run --project .\backend\Backend.csproj --no-launch-profile
start "Unreal Local Build Frontend" cmd /k cd /d "%FRONTEND_DIR%" ^&^& npm.cmd run dev

echo Backend:  http://localhost:5080
echo Frontend: http://localhost:5173
echo.
echo Close the opened terminal windows to stop the services.
goto :eof

:fail
echo.
echo Startup failed. Check the error output above.
exit /b 1

endlocal
