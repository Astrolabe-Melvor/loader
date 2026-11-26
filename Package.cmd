@echo off
setlocal enabledelayedexpansion
rem Builds ready-to-share zips into dist\: the loader and one zip per mod. Each zip extracts straight into the game
rem folder. "Package.cmd release" also uploads them to a GitHub Release (needs the gh CLI, logged in).
cd /d "%~dp0"
set "GAME=%MI2_GAME_DIR%"
if "%GAME%"=="" set "GAME=F:\SteamLibrary\steamapps\common\Melvor Idle 2"
if not exist "%GAME%\data_GamesByMalcs.Melvor2_windows_x86_64\GamesByMalcs.Melvor2.dll" (
  echo The game was not found in "%GAME%". The build needs its DLLs; set MI2_GAME_DIR.
  pause & exit /b 1
)
set "DIST=%~dp0dist"
set "STAGE=%DIST%\stage"
rem Old zips would be uploaded with the new ones; start clean.
if exist "%DIST%\*.zip" del /q "%DIST%\*.zip"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"

rem ---- the loader ----
for /f %%V in ('powershell -NoProfile -Command "([xml](Get-Content '%~dp0Loader\Loader.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"') do set "LVER=%%V"
set "L=%STAGE%\loader"
echo Building Astrolabe Loader %LVER%...
dotnet publish "%~dp0Loader\Loader.csproj" -c Release -o "%L%\data_MelvorModLoader_windows_x86_64" "-p:GameDir=%GAME%" -nologo -v:quiet
if errorlevel 1 ( echo Loader build failed. & pause & exit /b 1 )
rem createdump.exe (the runtime's crash-dump helper) is never used, and mod sites quarantine any .exe.
if exist "%L%\data_MelvorModLoader_windows_x86_64\createdump.exe" del /q "%L%\data_MelvorModLoader_windows_x86_64\createdump.exe"
(
  echo [dotnet]
  echo.
  echo project/assembly_name="MelvorModLoader"
) > "%L%\override.cfg"
mkdir "%L%\mods"
copy /y "%~dp0Release-README-Loader.txt" "%L%\Astrolabe Loader README.txt" >nul
call :zip "%L%" "%DIST%\AstrolabeLoader-%LVER%.zip"

rem ---- each mod, built against the loader just staged ----
for /d %%M in ("%~dp0Mods\*") do (
  if exist "%%M\%%~nxM.csproj" (
    set "MV="
    for /f %%V in ('powershell -NoProfile -Command "([xml](Get-Content '%%M\%%~nxM.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"') do set "MV=%%V"
    if "!MV!"=="" set "MV=0.0.0"
    set "M=%STAGE%\%%~nxM"
    echo Building %%~nxM !MV!...
    dotnet build "%%M" -c Release "-p:GameDir=%GAME%" "-p:LoaderOut=%L%\data_MelvorModLoader_windows_x86_64" "-p:OutputPath=!M!\mods\%%~nxM\\" -nologo -v:quiet
    if errorlevel 1 ( echo %%~nxM failed to build. & pause & exit /b 1 )
    if exist "%%M\README.md" copy /y "%%M\README.md" "!M!\mods\%%~nxM\README.md" >nul
    call :zip "!M!" "%DIST%\Astrolabe%%~nxM-!MV!.zip"
  )
)

rem ---- everything in one zip ----
set "ALL=%STAGE%\all"
mkdir "%ALL%"
for /d %%S in ("%STAGE%\*") do if /i not "%%~nxS"=="all" xcopy /e /i /y /q "%%S" "%ALL%" >nul
call :zip "%ALL%" "%DIST%\AstrolabeAll-%LVER%.zip"

echo.
echo Zips in %DIST%:
dir /b "%DIST%\*.zip"

if /i "%~1"=="release" (
  where gh >nul 2>nul || ( echo The gh CLI is needed to upload: https://cli.github.com & pause & exit /b 1 )
  set "TAG=v%LVER%"
  echo Uploading to the GitHub release !TAG! of Astrolabe-Melvor/loader...
  gh release view !TAG! -R Astrolabe-Melvor/loader >nul 2>nul || gh release create !TAG! -R Astrolabe-Melvor/loader -t "Astrolabe %LVER%" -F "%~dp0RELEASE-NOTES.md"
  gh release upload !TAG! "%DIST%\*.zip" -R Astrolabe-Melvor/loader --clobber
)
pause
exit /b 0

:zip
if exist "%~2" del "%~2"
powershell -NoProfile -Command "Compress-Archive -Path '%~1\*' -DestinationPath '%~2'"
exit /b 0
