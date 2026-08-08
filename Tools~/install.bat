@echo off
setlocal EnableDelayedExpansion
rem Install the upipe client on Windows.
rem
rem upipe is a shell script needing curl and python3, so it runs under WSL or Git
rem Bash rather than cmd. This picks whichever is present and installs there.
rem
rem   install.bat              install
rem   install.bat --uninstall  remove

set "SCRIPT_DIR=%~dp0"
set "SOURCE=%SCRIPT_DIR%upipe"

if not exist "%SOURCE%" (
    echo install.bat: upipe not found next to this script ^(looked in %SCRIPT_DIR%^) 1>&2
    exit /b 1
)

rem ---- WSL ------------------------------------------------------------------
rem Preferred when present: this package exists for the Unity-on-Windows,
rem agent-in-WSL setup, where the `unity` CLI cannot reach the editor because it
rem checks a PID that WSL cannot see.
where wsl >nul 2>&1
if %errorlevel%==0 (
    wsl -e test -f /proc/version >nul 2>&1
    if !errorlevel!==0 (
        echo Installing into WSL...
        for /f "usebackq delims=" %%p in (`wsl -e wslpath -a "%SCRIPT_DIR%"`) do set "WSLDIR=%%p"
        if "%~1"=="--uninstall" (
            wsl -e sh -c "sh '!WSLDIR!/install.sh' --uninstall"
        ) else (
            wsl -e sh -c "sh '!WSLDIR!/install.sh'"
        )
        exit /b !errorlevel!
    )
)

rem ---- Git Bash -------------------------------------------------------------
rem No WSL: fall back to Git for Windows, which ships the same interpreter and
rem curl. Installed under the user's Git Bash home so it lands on that PATH.
set "GITBASH="
if exist "%ProgramFiles%\Git\bin\bash.exe"      set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"
if exist "%ProgramFiles(x86)%\Git\bin\bash.exe" set "GITBASH=%ProgramFiles(x86)%\Git\bin\bash.exe"
if exist "%LOCALAPPDATA%\Programs\Git\bin\bash.exe" set "GITBASH=%LOCALAPPDATA%\Programs\Git\bin\bash.exe"

if defined GITBASH (
    echo Installing with Git Bash...
    if "%~1"=="--uninstall" (
        "%GITBASH%" -lc "sh \"$(cygpath -u '%SCRIPT_DIR%')/install.sh\" --uninstall"
    ) else (
        "%GITBASH%" -lc "sh \"$(cygpath -u '%SCRIPT_DIR%')/install.sh\""
    )
    exit /b %errorlevel%
)

rem ---- Neither --------------------------------------------------------------
echo. 1>&2
echo install.bat: neither WSL nor Git Bash was found. 1>&2
echo. 1>&2
echo upipe is a shell script and needs one of them, plus curl and python3. 1>&2
echo. 1>&2
echo   WSL      : wsl --install          ^(then re-run this script^) 1>&2
echo   Git Bash : https://git-scm.com/download/win 1>&2
echo. 1>&2
exit /b 1
