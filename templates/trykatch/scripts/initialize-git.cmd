@echo off
setlocal

where git >nul 2>nul
if errorlevel 1 (
  echo Git was not found. Install Git, then run git init in this directory. 1>&2
  exit /b 0
)

git rev-parse --is-inside-work-tree >nul 2>nul
if not errorlevel 1 (
  echo Git repository already available; initialization skipped.
  exit /b 0
)

git init --initial-branch=main
if errorlevel 1 exit /b %errorlevel%
echo Initialized Git repository with main as the initial branch.
