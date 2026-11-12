@echo off
wpeinit
rem High performance power plan - faster setup
powercfg /s 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c >nul 2>&1
rem Skip TPM, Secure Boot and RAM checks for Windows 11
reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f >nul
reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f >nul
reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f >nul
rem Find the app on the USB stick: WinFlash, or ReinstallKit on older sticks
set APP=
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do if not defined APP if exist %%d:\WinFlash\WinFlash.exe set APP=%%d:\WinFlash\WinFlash.exe
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do if not defined APP if exist %%d:\ReinstallKit\ReinstallKit.exe set APP=%%d:\ReinstallKit\ReinstallKit.exe
if defined APP (
  echo WinFlash: %APP%
  start "" "%APP%"
) else (
  echo WinFlash folder not found on any drive. Copy it to the root of the USB stick.
)
