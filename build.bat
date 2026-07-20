@echo off
echo ============================================
echo  Desktop Todo (WPF) — Build
echo ============================================
echo.
echo Prerequisites: .NET 8 SDK (https://dotnet.microsoft.com/download)
echo.

cd /d "%~dp0DesktopTodo"

echo Restoring packages...
dotnet restore

echo Building Release...
dotnet publish -c Release -o ..\publish --self-contained false -r win-x64

echo.
echo ============================================
echo  Output: ..\publish\DesktopTodo.exe
echo  Copy the entire publish\ folder to deploy.
echo ============================================
echo.
echo Size check:
dir ..\publish\DesktopTodo.exe 2>nul
echo.
pause
