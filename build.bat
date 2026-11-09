@echo off
echo ============================================
echo  Desktop Todo (WPF) - Build
echo ============================================
echo.
echo Prerequisites: .NET 8 SDK (https://dotnet.microsoft.com/download)
echo.

cd /d "%~dp0DesktopTodo"

REM Prefer the user-level SDK (used when Program Files is not writable).
REM Otherwise fall back to the dotnet on PATH.
set "DOTNET_EXE=dotnet"
if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET_EXE=%USERPROFILE%\.dotnet\dotnet.exe"
echo Using: %DOTNET_EXE%
echo.

echo Cleaning previous output (removes stale config.json / old binaries)...
if exist ..\publish rd /s /q ..\publish

echo Restoring packages...
"%DOTNET_EXE%" restore
if errorlevel 1 goto :fail

echo Publishing Release...
"%DOTNET_EXE%" publish -c Release -o ..\publish --self-contained false -r win-x64
if errorlevel 1 goto :fail

echo.
echo ============================================
echo  Output: ..\publish\DesktopTodo.exe
echo  config.json is NOT copied: fill in settings on first run.
echo  The app password is stored in Windows Credential Manager,
echo  never in config.json or in the publish folder.
echo ============================================
echo.
dir ..\publish\DesktopTodo.exe 2>nul
echo.
if "%~1"=="--no-pause" exit /b 0
pause
exit /b 0

:fail
echo.
echo BUILD FAILED - see the errors above. Output folder may be incomplete.
if "%~1"=="--no-pause" exit /b 1
pause
exit /b 1
