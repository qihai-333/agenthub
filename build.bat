@echo off
setlocal
chcp 65001 >nul

set "ROOT=%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

if not exist "%FW%\csc.exe" (
    echo [x] 找不到 .NET Framework 4 的编译器。
    echo     Windows 10 / 11 自带，如果你在精简系统上，请装 .NET Framework 4.8。
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
    echo [x] 编译失败，把上面的报错发给我。
    pause
    exit /b 1
)

echo.
echo [ok] 生成好了：%ROOT%bin\AgentHub.exe
echo.
echo      第一次运行会是一块黑底加一条空侧栏，点侧栏底部的「+」添加你的应用。
echo.
pause
