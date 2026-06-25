@echo off
REM ============================================================================
REM unregister.bat (publish-resident) - ships INTO publish\ beside
REM DbMcp.Server.exe (copied by the csproj). Removes dbmcp from every Claude
REM Desktop config on this machine, leaving all other servers untouched.
REM Self-contained: needs NO .NET SDK and NO source -- just this folder.
REM Idempotent. Restart Claude Desktop afterward.
REM ============================================================================
setlocal
cd /d "%~dp0"

if not exist "%~dp0DbMcp.Server.exe" (
  echo DbMcp.Server.exe not found beside this script.
  echo Run this from the published folder ^(it ships next to the exe^).
  endlocal
  exit /b 1
)

echo Removing dbmcp from Claude Desktop config(s)...
"%~dp0DbMcp.Server.exe" -unregister
set RC=%errorlevel%
echo.
if "%RC%"=="0" (echo Done. Restart Claude Desktop to drop dbmcp.) else (echo Unregister reported a problem - see the message above.)
endlocal & exit /b %RC%
