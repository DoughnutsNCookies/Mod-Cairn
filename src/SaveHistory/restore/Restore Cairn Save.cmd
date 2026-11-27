@echo off
rem Opens the Cairn save picker. Close the game first.
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Restore-CairnSave.ps1"
