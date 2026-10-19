@echo off
setlocal
rem Update brand\logo.jpg with the new logo staged in tools\logo.jpg.new
rem v2: clear read-only attribute + force-delete old file before copying.
set SRC=%~dp0tools\logo.jpg.new
set DST=%~dp0brand\logo.jpg

if not exist "%SRC%" (
    echo [x] Missing %SRC%
    pause
    exit /b 1
)

rem 1) remove read-only attribute on the old file (this was blocking the copy)
attrib -r "%DST%" >nul 2>&1

rem 2) force-delete the old file (/f also deletes read-only files)
del /f /q "%DST%" >nul 2>&1

rem 3) copy the new logo into place
copy /y "%SRC%" "%DST%" >nul
if errorlevel 1 (
    echo.
    echo [x] Still access denied. Do these then run this bat again:
    echo     1. Quit AGENT HUB if it is running
    echo     2. Close any Explorer window opened on the brand folder
    echo     3. Right-click update-logo.bat and choose Run as administrator
    pause
    exit /b 1
)

rem 4) verify size matches the staged file (49078 bytes)
for %%A in ("%DST%") do set NEWDST=%%~zA
if "%NEWDST%"=="49078" (
    echo [ok] brand\logo.jpg updated to the new logo ^(49078 bytes, verified^)
    del /q "%SRC%" >nul 2>&1
) else (
    echo [!] Copied but size looks odd: %NEWDST% bytes - kept tools\logo.jpg.new for retry
)
echo Done.
pause
