@echo off
REM ============================================================================
REM publish.bat - Build dbmcp (DbMcp.Server) as the self-contained exe that
REM carries the -register / -unregister verbs. Run after any server-code or
REM appsettings change. Safe to run from anywhere (uses its own folder).
REM ============================================================================
setlocal
cd /d "%~dp0"

echo Stopping any running DbMcp.Server.exe (a running instance locks the exe)...
taskkill /f /im DbMcp.Server.exe >nul 2>&1

echo Publishing DbMcp.Server (Release)...
dotnet publish "DbMcp.Server\DbMcp.Server.csproj" -c Release
if errorlevel 1 (
  echo.
  echo PUBLISH FAILED.
  echo If the error mentions the file is in use, a client re-spawned the server.
  echo Fully close Claude Desktop AND Claude Code, then run this again.
  endlocal
  exit /b 1
)

echo.
echo Publish OK -^> "%~dp0publish\DbMcp.Server.exe"
endlocal
exit /b 0
