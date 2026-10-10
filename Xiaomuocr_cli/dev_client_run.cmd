@echo off
setlocal EnableExtensions
title Xiaomu OCR - Local Client
cd /d "%~dp0src"

REM Force local backend (overrides SQLite saved production URL)
set XIAOMU_FORCE_SERVER_URL=http://127.0.0.1:8000

echo ========================================
echo   Local client dotnet run
echo ========================================
echo FORCED Server URL: %XIAOMU_FORCE_SERVER_URL%
echo   (ignores SQLite production address for this run)
echo.
echo Make sure local backend is up: Xiaomuocr_server\dev_start_backend.bat
echo.

where dotnet >nul 2>&1
if errorlevel 1 goto no_dotnet
dotnet run --project Xiaomuocr.App\Xiaomuocr.App.csproj
echo.
echo [INFO] client exited code=%ERRORLEVEL%
goto end
:no_dotnet
echo [ERROR] dotnet not found in PATH
:end
echo.
echo Type exit to close.
