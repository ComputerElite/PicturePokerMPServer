﻿@echo off
echo replacing frontend dir with the one from Debug
xcopy /Y /E /H /C /I frontend "bin\Debug\net6.0\frontend"
xcopy /Y /E /H /C /I frontend "bin\Release\net6.0\frontend"

set /p changelog=Changelog: 
echo Pushing to GitHub

git add *
git commit -m "%changelog%"
for /f %%i in ('git rev-parse HEAD') do set id=%%i
git push origin main

echo Pushed to GitHub

echo Commit id %id%

cd bin\Debug\net6.0\
echo Deleting existing update zip
del net6.0.zip
echo %id% > commit.txt

echo Creating new update zip
7z a net6.0.zip *.dll *.pdb *.exe *.json commit.txt frontend ref runtimes

echo Created update zip
echo Changelog:
echo.
echo %changelog%\nFull changes: https://github.com/ComputerElite/PicturePokerMPServer/commit/%id%

cd..
cd..
cd..

cd bin\Release\net6.0\
echo Deleting existing update zip
del net6.0.zip

echo Creating new update zip
7z a net6.0.zip *.dll *.pdb *.exe *.json frontend ref runtimes

echo Created update zip
cd..
cd..
cd..