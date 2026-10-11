@echo off
rem Reads the Tabletop Simulator table (read-only: nothing on the table is touched) and checks it against the
rem committed practice save, place by place (ticket 32). First load docs\research\tts-mod\ml-practice.json in TTS
rem and close any TTS code-editor plugin. The snapshot is also saved to runs\tts-live-snapshot.json.
if not exist "%~dp0..\runs" mkdir "%~dp0..\runs"
dotnet run --project "%~dp0..\engine\src\Advisor" -c Release -- tts-snapshot --compare "%~dp0..\tts\fixtures\ml-practice.snapshot.json" --count 5 --save "%~dp0..\runs\tts-live-snapshot.json" %*
pause
