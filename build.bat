@echo off
setlocal

rem Build AGENT HUB with the .NET Framework 4 compiler that ships with Windows 10/11.
rem This file is ASCII-only with CRLF endings on purpose: cmd misparses UTF-8 text + chcp.

set "ROOT=%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

if not exist "%FW%\csc.exe" (
    echo [x] .NET Framework 4 compiler not found.
    echo     Install .NET Framework 4.8 and try again.
    pause
    exit /b 1
)

if not exist "%ROOT%bin" mkdir "%ROOT%bin"

"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /out:"%ROOT%bin\AgentHub.exe" ^
  /win32manifest:"%ROOT%src\app.manifest" ^
  /win32icon:"%ROOT%icon.ico" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll ^
  /r:"%FW%\System.Web.Extensions.dll" ^
  "%ROOT%src\*.cs"

if errorlevel 1 (
    echo.
    echo [x] BUILD FAILED - send the errors above for a fix.
    pause
    exit /b 1
)

echo.
echo [ok] Built: %ROOT%bin\AgentHub.exe

rem ================= install to %USERPROFILE%\AgentHub =================
rem Without this step the desktop shortcut keeps launching the previous build.

set "INSTALL=%AGENTHUB_HOME%"
if "%INSTALL%"=="" set "INSTALL=%USERPROFILE%\AgentHub"

if not exist "%INSTALL%" mkdir "%INSTALL%"

taskkill /IM AgentHub.exe /F >nul 2>&1

copy /Y "%ROOT%bin\AgentHub.exe" "%INSTALL%\AgentHub.exe" >nul
if exist "%ROOT%brand\logo.png" copy /Y "%ROOT%brand\logo.png" "%INSTALL%\logo.png" >nul
if exist "%ROOT%agents.example.json" if not exist "%INSTALL%\agents.json" copy /Y "%ROOT%agents.example.json" "%INSTALL%\agents.json" >nul

if errorlevel 1 (
    echo [x] Install copy failed. Set AGENTHUB_HOME to the target folder and retry.
    pause
    exit /b 1
)

echo [ok] Installed: %INSTALL%\AgentHub.exe

echo.
echo      First run: empty sidebar. Click "+" at the bottom to add your apps.
echo.
pause
