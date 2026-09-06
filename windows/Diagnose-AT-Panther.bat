@echo off
REM =====================================================================
REM  AT Panther – Diagnose-Sammlung (windows/-Port)
REM  Sammelt: Systeminfos, Diagnose-Log und (optional, als Administrator)
REM  einen sxstrace-Ablauf zum Fehler "Side-by-Side-Konfiguration ungültig".
REM  Dieser Loader-Fehler entsteht in Windows VOR dem Programmstart – dann
REM  kann die App selbst nichts mehr protokollieren, daher dieses Skript.
REM  Beilage des Windows-Builds (GitHub Actions).
REM =====================================================================
setlocal EnableExtensions
chcp 65001 >nul

set "APPDIR=%LocalAppData%\ATPanther"
set "OUTDIR=%LocalAppData%\ATPanther\diagnose"
mkdir "%OUTDIR%" 2>nul

echo.
echo  [1/4] Systeminfos ...
>  "%OUTDIR%\sysinfo.txt" (
  echo AT Panther Diagnose-Sammlung – %DATE% %TIME%
  echo.
  ver
  echo.
  echo --- Windows-Version (PowerShell) ---
)
powershell -NoProfile -Command "Get-ComputerInfo -Property WindowsProductName,WindowsVersion,OsArchitecture | Format-List | Out-String -Width 200" >> "%OUTDIR%\sysinfo.txt" 2>nul
>> "%OUTDIR%\sysinfo.txt" (
  echo.
  echo --- ATPanther-Prozesse ---
)
tasklist /FI "IMAGENAME eq ATPanther.exe" >> "%OUTDIR%\sysinfo.txt" 2>nul
>> "%OUTDIR%\sysinfo.txt" (
  echo.
  echo --- Dateien neben diesem Skript ---
)
dir /-C "%~dp0" >> "%OUTDIR%\sysinfo.txt" 2>nul

echo  [2/4] Diagnose-Log kopieren ...
copy /Y "%APPDIR%\diagnostics.log"     "%OUTDIR%\" >nul 2>&1
copy /Y "%APPDIR%\diagnostics.*.log"   "%OUTDIR%\" >nul 2>&1
copy /Y "%APPDIR%\history.log"         "%OUTDIR%\" >nul 2>&1
copy /Y "%APPDIR%\monitor_state.json"  "%OUTDIR%\" >nul 2>&1
if exist "%OUTDIR%\diagnostics.log" (echo        Log gefunden.) else (echo        Noch kein diagnostics.log – App wurde evtl. nie gestartet.)

echo  [3/4] sxstrace (nur noetig bei "Side-by-Side-Konfiguration ungueltig") ...
net session >nul 2>&1
if errorlevel 1 (
  echo        KEIN Administrator: sxstrace wird uebersprungen.
  echo        Fuer die Loader-Analyse dieses Skript per Rechtsklick
  echo        "Als Administrator ausfuehren" neu starten.
) else (
  echo        Administrator OK – starte Ablaufverfolgung ...
  sxstrace trace -logfile:"%OUTDIR%\sxstrace.etl" >nul 2>&1
  echo.
  echo        *** Jetzt bitte AT Panther (ATPanther.exe) starten, ***
  echo        *** den Fehler reproduzieren und hierher zurueckkehren. ***
  echo.
  pause
  sxstrace parse -logfile:"%OUTDIR%\sxstrace.etl" -outfile:"%OUTDIR%\sxstrace.txt" >nul 2>&1
  if exist "%OUTDIR%\sxstrace.txt" (echo        sxstrace.txt geschrieben.) else (echo        sxstrace lieferte keine Ausgabe – ETL ggf. manuell pruefen.)
)

echo  [4/4] Fertig.
echo.
echo  Paket-Ordner: %OUTDIR%
echo  Bitte sysinfo.txt + diagnostics.log (+ ggf. sxstrace.txt) mitsenden.
explorer "%OUTDIR%"
endlocal
