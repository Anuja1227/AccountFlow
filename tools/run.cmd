@echo off
rem Runs tools\task.cmd and writes its output to tools\logs\last-run.log
cd /d "%~dp0.."
if not exist tools\logs mkdir tools\logs
echo [%date% %time%] Running task... > tools\logs\last-run.log
call tools\task.cmd >> tools\logs\last-run.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> tools\logs\last-run.log
echo [%date% %time%] Done. >> tools\logs\last-run.log
type tools\logs\last-run.log
timeout /t 8
