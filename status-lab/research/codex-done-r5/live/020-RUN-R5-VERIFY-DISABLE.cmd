@echo off
setlocal
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0K15-CODEX-DONE-R5-LIVE.ps1" -Mode VERIFY_DISABLE
exit /b %ERRORLEVEL%
