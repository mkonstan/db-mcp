@echo off
REM ============================================================================
REM unregister.bat - Remove dbmcp from EVERY Claude Desktop config on this
REM machine (classic + MSIX), leaving all other servers untouched. Restart
REM Claude Desktop afterward. Idempotent - safe to run when not registered.
REM ============================================================================
setlocal
cd /d "%~dp0"

if not exist "%~dp0publish\DbMcp.Server.exe" (
  echo Published exe not found at "%~dp0publish\DbMcp.Server.exe".
  echo Run publish.bat first, then re-run this.
  endlocal
  exit /b 1
)

echo Removing dbmcp from Claude Desktop config(s)...
"%~dp0publish\DbMcp.Server.exe" -unregister
if errorlevel 1 (
  echo.
  echo Unregister reported a problem - see the message above.
  endlocal
  exit /b 1
)

echo.
echo Done. Restart Claude Desktop to drop dbmcp.
endlocal
exit /b 0
