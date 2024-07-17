@REM https://github.com/socsieng/config-transform/tree/master
@REM usage transform.bat <file.config> <transformation> [output file]
@REM Where:
@REM <file.config> is the .Net configuration file that will be applied
@REM <transformation> is the name of the transformation to apply (typically Debug or Release)
@REM assumption: corresponding file.debug.config or file.release.config file exists in the same directory as file.config
@REM [output file] is the optional file name to outupt the resultant transformation to
@REM if unspecified, the contents will be sent to command window (stdout)

@echo off
call :transform %1 %2 %3
exit /b

:transform
set transform_drive=%~d1
set transform_path=%~p1
set transform_file=%~nx1
set transform_name=%~n1
set transform_ext=%~x1
set transform_fullpath=%transform_drive%%transform_path%
set transform_output=%transform_fullpath%transform.%transform_name%.%2%transform_ext%

if not "%3"=="" (
	set transform_output=%3
)

echo %transform_output%

echo ^<Project ToolsVersion="4.0" DefaultTargets="Release" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"^>^<UsingTask TaskName="TransformXml" AssemblyFile="$(MSBuildExtensionsPath)\Microsoft\VisualStudio\v10.0\Web\Microsoft.Web.Publishing.Tasks.dll"/^>^<Target Name="Release"^>^<TransformXml Source="%transform_fullpath%%transform_file%" Transform="%transform_fullpath%%transform_name%.%2%transform_ext%" Destination="%transform_output%"/^>^</Target^>^</Project^> > .transform.proj

if "%3"=="" (
	msbuild .transform.proj > nul
	type "%transform_output%"
	del "%transform_output%"
) else (
	msbuild .transform.proj
)

del .transform.proj

exit /b
