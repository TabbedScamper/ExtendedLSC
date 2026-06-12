@echo off
setlocal

:: Find VS installation
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do set VS_PATH=%%i

if "%VS_PATH%"=="" (
    echo ERROR: Visual Studio not found
    exit /b 1
)

echo Found Visual Studio at: %VS_PATH%

:: Initialize VS environment
call "%VS_PATH%\Common7\Tools\VsDevCmd.bat" -arch=amd64
if errorlevel 1 (
    echo ERROR: Failed to initialize VS environment
    exit /b 1
)

echo.
echo Compiler:
cl 2>&1 | findstr /C:"Version"

:: Check CMake
cmake --version
if errorlevel 1 (
    echo ERROR: CMake not found. Please install CMake.
    exit /b 1
)

:: Navigate to WheelFitmentAPI directory
cd /d "%~dp0WheelFitmentAPI"
if not exist "WheelFitmentAPI.cpp" (
    echo ERROR: WheelFitmentAPI.cpp not found
    exit /b 1
)

:: Create build directory
if not exist "build" mkdir build
cd build

:: Configure with CMake
echo.
echo Configuring with CMake...
cmake .. -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 (
    echo ERROR: CMake configuration failed
    exit /b 1
)

:: Build
echo.
echo Building...
cmake --build . --config Release
if errorlevel 1 (
    echo ERROR: Build failed
    exit /b 1
)

:: Check output
echo.
if exist "WheelFitmentAPI.asi" (
    echo SUCCESS: WheelFitmentAPI.asi built successfully!
    dir WheelFitmentAPI.asi
) else (
    echo ERROR: Output file not found
    exit /b 1
)

echo.
echo Done!
