@echo off
rem Build FloatingKanban.exe with the built-in .NET Framework compiler
cd /d %~dp0
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set WPFDIR=%FW%\WPF

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:FloatingKanban.exe /win32manifest:app.manifest /win32icon:app.ico /r:"%WPFDIR%\PresentationCore.dll" /r:"%WPFDIR%\PresentationFramework.dll" /r:"%WPFDIR%\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:System.Web.Extensions.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.dll /r:System.Core.dll /r:System.Xml.dll Kanban.cs

if exist FloatingKanban.exe (echo Build OK: %~dp0FloatingKanban.exe) else (echo Build FAILED)
