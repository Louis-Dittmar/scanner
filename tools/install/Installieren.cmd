@echo off
rem Installiert Scanner Paperless: vertraut dem mitgelieferten Zertifikat (fragt nach Administratorrechten)
rem und installiert App und Abhaengigkeiten. Danach im Startmenue "Scanner Paperless" oeffnen.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
pause
