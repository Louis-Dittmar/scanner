@echo off
rem Entfernt Scanner Paperless samt App-Daten. Liegt die KI-Texterkennung im Standardordner, wird sie mit entfernt.
rem Ein selbst gewaehlter KI-Ordner ("Scanner Paperless KI") bleibt erhalten und kann vorher in den Einstellungen entfernt werden.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-AppxPackage LouisDittmar.ScannerPaperless | Remove-AppxPackage; if ($?) { Write-Host Scanner Paperless wurde entfernt. }"
pause
