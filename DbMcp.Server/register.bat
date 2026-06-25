@echo off
REM ============================================================================
REM register.bat (publish-resident) - ships INTO publish\ beside DbMcp.Server.exe
REM (copied by the csproj). Registers dbmcp into every Claude Desktop config on
REM this machine. Self-contained: the exe is self-contained win-x64, so this
REM needs NO .NET SDK and NO source -- just this folder. Restart Claude Desktop
REM afterward. This bat ships in the published folder and only registers the
REM sibling DbMcp.Server.exe -- it does NOT build. To (re)build the exe from
REM source, run publish.bat (in src\), which is the build tool.
REM ============================================================================
setlocal
cd /d "%~dp0"

if not exist "%~dp0DbMcp.Server.exe" (
  echo DbMcp.Server.exe not found beside this script.
  echo Run this from the published folder ^(it ships next to the exe^).
  endlocal
  exit /b 1
)

echo Registering dbmcp into Claude Desktop config(s)...
"%~dp0DbMcp.Server.exe" -register
set RC=%errorlevel%
echo.
if "%RC%"=="0" (echo Done. Restart Claude Desktop to load dbmcp.) else (echo Register reported a problem - see the message above.)
endlocal & exit /b %RC%
