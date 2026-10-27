@echo off
rem ============================================================
rem  CoPul self-CPU measurement (60s window x 6 runs)
rem
rem  BEFORE RUNNING: close heavy apps (java / games / IDE / builds).
rem  Rule: Task Manager CPU utilization must be under 10%,
rem  otherwise the numbers are meaningless (see docs/M1.2 ch.6).
rem
rem  Output: perf-1.txt .. perf-6.txt in this folder.
rem ============================================================
setlocal
cd /d "%~dp0"

set "DOTNET=%USERPROFILE%\.workbuddy\binaries\dotnet-sdk\8.0.404\dotnet.exe"
if not exist "%DOTNET%" (
    where dotnet >nul 2>nul
    if errorlevel 1 (
        echo [ERROR] dotnet not found. Edit DOTNET= in this file.
        pause
        exit /b 1
    )
    set "DOTNET=dotnet"
)

set "APP=..\..\src\CoPul.App\bin\Release\net8.0-windows\CoPul.dll"
if not exist "%APP%" (
    echo [ERROR] build first:  dotnet build CoPul.sln -c Release
    pause
    exit /b 1
)

echo Measuring 6 x 60s. Do not touch the machine.
echo.

for %%i in (1 2 3 4 5 6) do (
    echo ---- run %%i / 6 ----
    "%DOTNET%" "%APP%" --stats "perf-%%i.txt" --seconds 60 --warmup 10
    findstr /B "single-core machine frames" "perf-%%i.txt"
    echo.
)

echo Done. Compare the median and the worst value against 1.5%% / 2.5%%.
pause
