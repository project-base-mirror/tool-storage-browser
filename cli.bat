@echo off
setlocal
set "S3EXPLORER_ROOT=%~dp0"
dotnet run --project "%S3EXPLORER_ROOT%src\S3Explorer.Cli\S3Explorer.Cli.csproj" -- %*
exit /b %ERRORLEVEL%
