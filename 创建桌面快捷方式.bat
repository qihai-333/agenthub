@echo off
setlocal
rem Create AGENT HUB desktop shortcuts (no UAC prompt on every launch).
rem This file is ASCII-only on purpose: cmd misparses UTF-8 text + chcp.
powershell -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0tools\Install-AgentHubShortcut.ps1"
pause
