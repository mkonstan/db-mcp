@echo off
REM ============================================================================
REM register.bat - Publish (fresh) then register dbmcp into EVERY Claude Desktop
REM config on this machine (classic + MSIX). One-stop install. Restart Claude
REM Desktop afterward to pick it up. Backs up each config before writing.
REM ============================================================================
setlocal
cd /d "%~dp0"

call "%~dp0publish.bat"
if errorlevel 1 (
  echo Skipping register because publish failed.
  endlocal
  exit /b 1
)

echo.
echo Registering dbmcp into Claude Desktop config(s)...
"%~dp0publish\DbMcp.Server.exe" -register
if errorlevel 1 (
  echo.
  echo Register reported a problem - see the message above.
  endlocal
  exit /b 1
)

echo.
echo Done. Restart Claude Desktop to load dbmcp.
endlocal
exit /b 0
