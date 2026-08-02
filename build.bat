@echo off
dotnet publish -c Release -r win-x64 --self-contained false -o dist

if %errorlevel% neq 0 (echo BUILD FAILED) else (echo BUILD SUCCEEDED)
pause