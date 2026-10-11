@echo off
rem Starts the Advisor Window (spec A8) and opens it in the browser. Double-click this file, or run it from a terminal
rem with options: advisor.cmd --port 7701 --no-browser, or --agent neural:best.onnx for advice from a network.
rem Close this window (or Ctrl+C) to stop; games are saved.
dotnet run --project "%~dp0..\engine\src\Advisor" -c Release -- %*
if errorlevel 1 pause
