@echo off
rem Starts the Advisor Window with the newest trained networks giving the advice (and playing in "Play against the AI"):
rem   base games:     the main Run's best Generation (C:\7wd-runs\main\best.json)
rem   Pantheon games: the Pantheon Run's best Generation (C:\7wd-runs\pantheon\best.json)
rem Both are read from best.json at every start, so this keeps up with further training. Extra options pass through
rem (e.g. advisor-latest.cmd --port 7701). Close this window (or Ctrl+C) to stop; games are saved.
setlocal
set "RUNS=%SEVENWD_RUNS%"
if "%RUNS%"=="" set "RUNS=C:\7wd-runs"
set "BASE="
set "PANTHEON="
for /f "usebackq delims=" %%m in (`powershell -NoProfile -Command "(Get-Content '%RUNS%\main\best.json' -Raw | ConvertFrom-Json).model"`) do set "BASE=%RUNS%\main\%%m"
for /f "usebackq delims=" %%m in (`powershell -NoProfile -Command "(Get-Content '%RUNS%\pantheon\best.json' -Raw | ConvertFrom-Json).model"`) do set "PANTHEON=%RUNS%\pantheon\%%m"
if "%BASE%"=="" (echo Couldn't read %RUNS%\main\best.json & pause & exit /b 1)
if "%PANTHEON%"=="" (echo Couldn't read %RUNS%\pantheon\best.json & pause & exit /b 1)
set "BASE=%BASE:/=\%"
set "PANTHEON=%PANTHEON:/=\%"
echo Base games:     %BASE%
echo Pantheon games: %PANTHEON%
dotnet run --project "%~dp0..\engine\src\Advisor" -c Release -- --agent "neural:%BASE%,mode=classic" --pantheon-agent "neural:%PANTHEON%,mode=classic" %*
if errorlevel 1 pause
