@echo off
chcp 65001 >nul
title ControlPhone - server (closes by itself when you close the app window)
cd /d "%~dp0"

rem Dung node.exe di kem (ban phat hanh) neu co, neu khong dung Node.js da cai
set "NODE=node"
if exist "%~dp0node\node.exe" set "NODE=%~dp0node\node.exe"
"%NODE%" -v >nul 2>nul || (echo Please install Node.js 18+ from https://nodejs.org & pause & exit /b 1)

if not exist "node_modules\ws" (
  echo Installing dependencies...
  call npm install --omit=dev || (pause & exit /b 1)
)
if not exist "vendor\scrcpy-win64-v5.0\scrcpy-server" (
  "%NODE%" tools\setup.js || (pause & exit /b 1)
)

"%NODE%" server\index.js --open
rem chi giu cua so khi co loi (ma thoat khac 0)
if errorlevel 1 pause