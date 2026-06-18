@echo off
cd /d "%~dp0"

:: Сборка если нужно
dotnet build -c Debug -v quiet 2>nul

:: Запуск exe напрямую — без пауз, без | more
start "" "bin\Debug\net8.0-windows\HachBobAI.exe"
