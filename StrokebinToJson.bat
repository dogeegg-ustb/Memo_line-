@echo off
REM 编译并运行根目录 StrokebinToJson 工具
setlocal
cd /d "%~dp0StrokebinToJson"

dotnet build -c Release
if errorlevel 1 (
  echo 编译失败
  pause
  exit /b 1
)

REM 若有拖放参数则转发；否则进入监视模式
if "%~1"=="" (
  dotnet run -c Release --no-build
) else (
  dotnet run -c Release --no-build -- %*
)

endlocal
