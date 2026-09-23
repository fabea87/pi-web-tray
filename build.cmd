@echo off
rem Builds pi-web-tray.exe from src\ using the in-box .NET Framework compiler.
setlocal
set "ROOT=%~dp0"
set "PF86=%ProgramFiles(x86)%"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  for /f "usebackq delims=" %%i in (`"%PF86%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find MSBuild\**\Bin\Roslyn\csc.exe`) do set "CSC=%%i"
)
if not exist "%CSC%" (
  echo Could not find a C# compiler. Install .NET Framework 4.x or Visual Studio Build Tools.
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ ^
  /out:"%ROOT%pi-web-tray.exe" ^
  /win32icon:"%ROOT%assets\pi-web-tray.ico" ^
  /win32manifest:"%ROOT%src\app.manifest" ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Management.dll ^
  "%ROOT%src\pi-web-tray.cs" ^
  "%ROOT%src\upgrade-form.cs" ^
  "%ROOT%src\paths.cs" ^
  "%ROOT%src\AssemblyInfo.cs"

if errorlevel 1 (
  echo Build FAILED
  exit /b 1
)
echo Build OK: %ROOT%pi-web-tray.exe
endlocal
