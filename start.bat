@echo off
rem ===========================================================================
rem  Football Simulation - fast launcher
rem  Double-click for the game, or pass a command (see "start.bat help").
rem  Note: in cmd, type "start.bat" WITH the extension - "start" alone is a
rem  cmd built-in (it opens a new window).
rem ===========================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "SLN=FootballSimulation.sln"
set "TESTS=tests\FootballSim.Tests\FootballSim.Tests.csproj"
set "PROBE=tools\FootballSim.AuditProbe\FootballSim.AuditProbe.csproj"
set "GAME_PROJECT=src\FootballSim.Godot"
set "GODOT_DEFAULT=C:\Games\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"

rem Stay open when double-clicked with no arguments (so errors can be read).
if "%~1"=="" set "PAUSE_AT_END=1"

if "%~1"==""        goto play
if /i "%~1"=="play"   goto play
if /i "%~1"=="test"   goto test
if /i "%~1"=="check"  goto check
if /i "%~1"=="verify" goto verify
if /i "%~1"=="build"  goto build
if /i "%~1"=="match"  goto match11
if /i "%~1"=="11v11"  goto match11
if /i "%~1"=="5v5"    goto match5
if /i "%~1"=="lab"    goto lab
if /i "%~1"=="probe"  goto probe
if /i "%~1"=="help"   goto help
if /i "%~1"=="?"      goto help
if /i "%~1"=="-h"     goto help
if /i "%~1"=="/?"     goto help
goto unknown

rem ---------------------------------------------------------------------------
:play
call :find_godot
if not defined GODOT_BIN goto no_godot
echo.
echo   Building game assembly (incremental) ...
dotnet build "%GAME_PROJECT%\FootballSim.Godot.csproj" --nologo -v q
if errorlevel 1 goto failed
echo.
echo   Launching FootballSimulation in Godot 4.7.2 ...
echo   (%GODOT_BIN%)
echo.
"%GODOT_BIN%" --path "%GAME_PROJECT%" %2 %3 %4 %5
goto done

rem ---------------------------------------------------------------------------
:test
echo.
echo   Running the test suite ...
echo.
dotnet test "%TESTS%" --nologo -v q
goto done

rem ---------------------------------------------------------------------------
:check
echo.
echo   Building the solution ...
echo.
dotnet build "%SLN%" --nologo -v q
if errorlevel 1 goto failed
echo.
echo   Running the test suite ...
echo.
dotnet test "%TESTS%" --nologo -v q
goto done

rem ---------------------------------------------------------------------------
:verify
echo.
echo   Building the solution ...
echo.
dotnet build "%SLN%" --nologo -v q
if errorlevel 1 goto failed
echo.
echo   Running the test suite ...
echo.
dotnet test "%TESTS%" --nologo -v q
if errorlevel 1 goto failed
echo.
echo   Running the audit probe (writes artifacts\audit-probe-latest.json) ...
echo.
dotnet build "%PROBE%" --nologo -v q
if errorlevel 1 goto failed
dotnet run --project "%PROBE%" --no-build > "artifacts\audit-probe-latest.json"
echo   Probe written to artifacts\audit-probe-latest.json
goto done

rem ---------------------------------------------------------------------------
:build
echo.
echo   Building the solution ...
echo.
dotnet build "%SLN%" --nologo -v q
goto done

rem ---------------------------------------------------------------------------
:match11
echo.
echo   Headless 11v11 match (Ctrl+C to stop) ...
echo.
dotnet run --project src\FootballSim.Headless -- --match-11v11
goto done

rem ---------------------------------------------------------------------------
:match5
echo.
echo   Headless 5v5 match (Ctrl+C to stop) ...
echo.
dotnet run --project src\FootballSim.Headless -- --match-5v5
goto done

rem ---------------------------------------------------------------------------
:lab
echo.
echo   Ball physics lab ...
echo.
dotnet run --project src\FootballSim.Headless -- --physics-lab
goto done

rem ---------------------------------------------------------------------------
:probe
echo.
echo   Building and running the audit probe ...
echo.
dotnet build "%PROBE%" --nologo -v q
if errorlevel 1 goto failed
dotnet run --project "%PROBE%" --no-build > "artifacts\audit-probe-latest.json"
echo   Probe written to artifacts\audit-probe-latest.json
goto done

rem ---------------------------------------------------------------------------
:help
echo.
echo   Football Simulation launcher
echo   ----------------------------
echo     start.bat            build-free launch of the Godot game
echo     start.bat play       same as above
echo     start.bat check      build the solution, then run the tests
echo     start.bat test       run the tests only
echo     start.bat verify     build + tests + audit probe
echo     start.bat build      build the solution only
echo     start.bat match      headless 11v11 match
echo     start.bat 5v5        headless 5v5 match
echo     start.bat lab        ball physics lab (writes a CSV)
echo     start.bat probe      audit probe (writes JSON)
echo.
echo   Godot is found from %%GODOT_EXE%%, then this known path:
echo     %GODOT_DEFAULT%
echo   Override for another machine with:  set GODOT_EXE=C:\path\to\Godot.exe
echo.
goto done

rem ---------------------------------------------------------------------------
:no_godot
echo.
echo   [ERROR] Godot 4.7.2 .NET executable not found.
echo.
echo   Looked for:
echo     %%GODOT_EXE%%  (not set or missing)
echo     %GODOT_DEFAULT%
echo     godot.exe / godot4.exe on PATH
echo.
echo   Fix it once for this window:
echo     set GODOT_EXE=C:\path\to\Godot_v4.7.2-stable_mono_win64.exe
echo   ...or copy that line into the GODOT_DEFAULT value in this file.
echo.
set "RC=1"
goto done

rem ---------------------------------------------------------------------------
:unknown
echo.
echo   [ERROR] Unknown command: %~1
set "RC=1"
goto help

rem ---------------------------------------------------------------------------
:failed
echo.
echo   [ERROR] The step above failed. See the output.
set "RC=1"
goto done

rem ---------------------------------------------------------------------------
rem  Sets GODOT_BIN from GODOT_EXE, the pinned default, or PATH.
:find_godot
set "GODOT_BIN="
if defined GODOT_EXE if exist "%GODOT_EXE%" set "GODOT_BIN=%GODOT_EXE%"
if defined GODOT_BIN goto :eof
if exist "%GODOT_DEFAULT%" set "GODOT_BIN=%GODOT_DEFAULT%"
if defined GODOT_BIN goto :eof
for /f "delims=" %%P in ('where godot.exe 2^>nul') do set "GODOT_BIN=%%P"
if defined GODOT_BIN goto :eof
for /f "delims=" %%P in ('where godot4.exe 2^>nul') do set "GODOT_BIN=%%P"
goto :eof

rem ---------------------------------------------------------------------------
:done
if not defined RC set "RC=%ERRORLEVEL%"
if defined PAUSE_AT_END pause
exit /b %RC%
