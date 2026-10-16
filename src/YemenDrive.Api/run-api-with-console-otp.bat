@echo off
setlocal
cd /d "%~dp0"
set "YEMENDRIVE_ENABLE_CONSOLE_OTP=1"
echo YemenDrive API: temporary console OTP mode is enabled for this process.
echo Keep this console open to read OTP codes.
echo Do not expose this temporary mode to a public server.
YemenDrive.Api.exe
endlocal
