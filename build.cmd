@echo off
setlocal

rem  DesktopSwitcher build script.
rem
rem    build.cmd            -> windowed build (no console), for normal use
rem    build.cmd console    -> console build, for milestone testing / selftest
rem
rem  Targets the in-box .NET Framework compiler, so nothing needs installing.
rem  NOTE: that compiler is C# 5 only - no string interpolation, nameof, ?. etc.

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "OUTDIR=%LOCALAPPDATA%\DesktopSwitcher"
set "OUTEXE=%OUTDIR%\DesktopSwitcher.exe"

set "TARGET=winexe"
if /i "%~1"=="console" set "TARGET=exe"

if not exist "%CSC%" (
  echo ERROR: C# compiler not found at:
  echo        %CSC%
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo Building [%TARGET%] -^> %OUTEXE%

"%CSC%" -nologo -target:%TARGET% -platform:x64 -optimize+ ^
  -out:"%OUTEXE%" ^
  -r:System.dll ^
  -r:System.Core.dll ^
  -r:System.Drawing.dll ^
  -r:System.Windows.Forms.dll ^
  "%~dp0src\*.cs"

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

echo BUILD OK
exit /b 0
