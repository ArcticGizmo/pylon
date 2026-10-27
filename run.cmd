@echo off
rem Builds and runs the dev (Debug) tray app, Pylon (Dev) on pipe pylon-dev, calling out through Postern (Dev).
rem
rem   run                              start the dev tray
rem   run --headless                   broker and monitors only, no tray
rem   run bench                        start the test bench (fake GitHub on 127.0.0.1:5610) instead
rem
rem Quit any Pylon (Dev) that's already running first: it locks the bin folder, so the build can't replace it.
rem The "--" keeps the app's arguments away from `dotnet run`.
if /i "%~1"=="bench" (
  dotnet run --project "%~dp0tools\Pylon.TestBench" -- %2 %3 %4 %5
) else (
  dotnet run --project "%~dp0src\Pylon.App" -- %*
)
