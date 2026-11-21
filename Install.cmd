@echo off
setlocal
rem Builds the loader into the game folder, writes override.cfg, then builds every mod under Mods\.
set "GAME=%MI2_GAME_DIR%"
if "%GAME%"=="" set "GAME=F:\SteamLibrary\steamapps\common\Melvor Idle 2"
if not exist "%GAME%\Melvor2.exe" (
  echo Melvor2.exe was not found in "%GAME%".
  echo Set MI2_GAME_DIR to your game folder and run this again.
  pause & exit /b 1
)
tasklist /FI "IMAGENAME eq Melvor2.exe" 2>nul | find /I "Melvor2.exe" >nul && (
  echo Close Melvor Idle 2 first.
  pause & exit /b 1
)
where dotnet >nul 2>nul || (
  echo The .NET 10 SDK is needed: https://dotnet.microsoft.com/download
  pause & exit /b 1
)

echo Building the loader...
dotnet publish "%~dp0Loader\Loader.csproj" -c Release -o "%GAME%\data_MelvorModLoader_windows_x86_64" "-p:GameDir=%GAME%" -nologo -v:minimal
if errorlevel 1 ( echo Loader build failed. & pause & exit /b 1 )

(
  echo [dotnet]
  echo.
  echo project/assembly_name="MelvorModLoader"
) > "%GAME%\override.cfg"
if not exist "%GAME%\mods" mkdir "%GAME%\mods"

for /d %%M in ("%~dp0Mods\*") do (
  if exist "%%M\*.csproj" (
    echo Building %%~nxM...
    dotnet build "%%M" -c Release "-p:GameDir=%GAME%" -nologo -v:minimal
    if errorlevel 1 ( echo %%~nxM failed to build. & pause & exit /b 1 )
  )
)

echo.
echo Installed. Start the game from Steam; a green "Astrolabe Loader" tag shows top-left. F1 opens the Mods menu.
pause
