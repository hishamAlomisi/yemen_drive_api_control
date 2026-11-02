@echo off
setlocal
cd /d "%~dp0"
set "YEMENDRIVE_ENABLE_CONSOLE_OTP=1"
echo YemenDrive API: temporary console OTP mode is enabled for this process.
echo Keep this console open to read OTP codes.
echo Do not expose this temporary mode to a public server.
"%~dp0YemenDrive.Api.exe"
set "YEMENDRIVE_API_EXIT_CODE=%ERRORLEVEL%"
if not "%YEMENDRIVE_API_EXIT_CODE%"=="0" (
    echo.
    echo YemenDrive API stopped with error code %YEMENDRIVE_API_EXIT_CODE%.
    echo Read the error above before closing this window.
    pause
)
exit /b %YEMENDRIVE_API_EXIT_CODE%
