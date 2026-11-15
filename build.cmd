@echo off
rem Builds dist\RobloxProductManager.exe, the Windows desktop app.
rem Uses the C# compiler that ships with Windows (.NET Framework 4.x), so nothing needs to be installed or downloaded.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework C# compiler ^(csc.exe^).
  exit /b 1
)
if not exist "%~dp0dist" mkdir "%~dp0dist"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu32bitpreferred ^
  /out:"%~dp0dist\RobloxProductManager.exe" ^
  /win32manifest:"%~dp0app\app.manifest" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  "%~dp0app\*.cs"
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
echo Built dist\RobloxProductManager.exe
