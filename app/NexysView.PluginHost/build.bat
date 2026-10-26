@echo off
rem Builds NexysView.PluginHost.exe (public the vendor DataPlugin SDK ABI worker, see tools/dp_minihost) -> app\NexysView.PluginHost\bin\
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul || exit /b 1
set HERE=%~dp0
set SDK=%HERE%..\..\ref\dataplugin-sdk-cpp
if not exist "%HERE%bin" mkdir "%HERE%bin"
cd /d "%HERE%bin"
cl /nologo /EHsc /O2 /MD /std:c++17 /W3 /DUNICODE /D_UNICODE /I"%SDK%\include" "%HERE%PluginHost.cpp" /Fe:NexysView.PluginHost.exe /link ole32.lib oleaut32.lib || exit /b 2
del *.obj 2>nul
echo BUILD OK
