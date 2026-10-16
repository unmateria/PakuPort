@echo off
rem `pakuport <command>`, run from the repository root.
rem Uses the prebuilt bin\ of a release download if it is there; otherwise builds and runs from src\.
if exist "%~dp0bin\PakuPort.Cli.exe" (
  "%~dp0bin\PakuPort.Cli.exe" %*
) else (
  dotnet run -c Release --project "%~dp0src\PakuPort.Cli" -- %*
)
