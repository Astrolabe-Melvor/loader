@echo off
setlocal
rem Removes the loader. The game runs untouched again. Your mods folder is kept.
set "GAME=%MI2_GAME_DIR%"
if "%GAME%"=="" set "GAME=F:\SteamLibrary\steamapps\common\Melvor Idle 2"
del /q "%GAME%\override.cfg" 2>nul
rmdir /s /q "%GAME%\data_MelvorModLoader_windows_x86_64" 2>nul
echo Loader removed. The mods folder was left in place.
pause
