@echo off

call "%VS120COMNTOOLS%vsvars32.bat" || goto :error

devenv /build Release iFix.sln || goto :error

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
