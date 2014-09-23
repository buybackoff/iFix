@echo off

REM Run this script from the root of iFix (the current directory should
REM contain iFix.sln). It will build the whole solution and push new
REM versions of iFix.Core, iFix.Mantle and iFix.Crust to nuget.org.

call "%VS120COMNTOOLS%vsvars32.bat" || goto :error

devenv /rebuild Release iFix.sln || goto :error

cd Core || goto :error
if exist *.nupkg (del *.nupkg || goto :error)
nuget pack Core.csproj -IncludeReferencedProjects -Prop Configuration=Release || goto :error
nuget push *.nupkg || goto :error
cd .. || goto :error

cd Mantle || goto :error
if exist *.nupkg (del *.nupkg || goto :error)
nuget pack Mantle.csproj -IncludeReferencedProjects -Prop Configuration=Release || goto :error
nuget push *.nupkg || goto :error
cd .. || goto :error

cd Crust || goto :error
if exist *.nupkg (del *.nupkg || goto :error)
nuget pack Crust.csproj -IncludeReferencedProjects -Prop Configuration=Release || goto :error
nuget push *.nupkg || goto :error
cd .. || goto :error

goto :EOF
:error
echo Failed with error #%errorlevel%.
exit /b %errorlevel%
