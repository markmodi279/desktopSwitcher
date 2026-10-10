@echo off
setlocal

rem  DesktopSwitcher build script.
rem
rem    build.cmd            -> windowed build (no console), for normal use
rem    build.cmd console    -> console build, for milestone testing / selftest
rem    build.cmd release    -> windowed build into dist\, zipped with a SHA256, to hand out
rem
rem  Targets the in-box .NET Framework compiler, so nothing needs installing.
rem  NOTE: that compiler is C# 5 only - no string interpolation, nameof, ?. etc.
rem
rem  -recurse: rather than a plain wildcard, so src\ can be foldered by layer.
rem  A plain "src\*.cs" does not descend, and would silently compile nothing.
rem
rem  -win32icon: the file icon Explorer and Task Manager show. It is the same four-square
rem  mark Controller.CreateIcon draws for the tray, generated once and committed under
rem  assets\ - there is no build step that could redraw it, and no reason for one.
rem  Version metadata is not here: csc stamps that from the attributes in
rem  src\app\AssemblyInfo.cs.

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "OUTDIR=%LOCALAPPDATA%\DesktopSwitcher"
set "OUTEXE=%OUTDIR%\DesktopSwitcher.exe"

set "TARGET=winexe"
if /i "%~1"=="console" set "TARGET=exe"

rem  release builds go to dist\, never the live install path: building over a running
rem  exe fails, and a release should not disturb the copy in use.
set "RELEASE="
if /i "%~1"=="release" (
  set "RELEASE=1"
  set "OUTDIR=%~dp0dist\stage"
  set "OUTEXE=%~dp0dist\stage\DesktopSwitcher.exe"
)

if not exist "%CSC%" (
  echo ERROR: C# compiler not found at:
  echo        %CSC%
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo Building [%TARGET%] -^> %OUTEXE%

"%CSC%" -nologo -target:%TARGET% -platform:x64 -optimize+ ^
  -out:"%OUTEXE%" ^
  -win32icon:"%~dp0assets\DesktopSwitcher.ico" ^
  -r:System.dll ^
  -r:System.Core.dll ^
  -r:System.Drawing.dll ^
  -r:System.Windows.Forms.dll ^
  -recurse:"%~dp0src\*.cs"

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

echo BUILD OK
if not defined RELEASE exit /b 0

rem  --- release packaging --------------------------------------------------------
rem  The version comes from the exe itself (AssemblyInfo.cs), so the zip name can never
rem  disagree with what Explorer shows. PowerShell is in the box, like the compiler.
set "VER="
for /f "delims=" %%V in ('powershell -NoProfile -Command "(Get-Item '%OUTEXE%').VersionInfo.FileVersion"') do set "VER=%%V"
if not defined VER (
  echo ERROR: could not read the version from %OUTEXE%
  exit /b 1
)

set "ZIP=%~dp0dist\DesktopSwitcher-%VER%.zip"
copy /y "%~dp0LICENSE" "%OUTDIR%\LICENSE" >nul
copy /y "%~dp0docs\INSTALL.txt" "%OUTDIR%\README.txt" >nul
if exist "%ZIP%" del "%ZIP%"

powershell -NoProfile -Command "Compress-Archive -Path '%OUTDIR%\*' -DestinationPath '%ZIP%'; $h = (Get-FileHash -Algorithm SHA256 '%ZIP%').Hash.ToLower(); Set-Content -Encoding ASCII -Path '%ZIP%.sha256' -Value ($h + '  DesktopSwitcher-%VER%.zip'); Write-Host ('SHA256 ' + $h)"
if errorlevel 1 (
  echo.
  echo PACKAGING FAILED
  exit /b 1
)

echo RELEASE OK -^> %ZIP%
exit /b 0
