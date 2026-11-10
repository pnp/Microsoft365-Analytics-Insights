## AutomationPS
The Azure Automation runbooks that run the SQL `profiling` aggregation (see `App.ControlPanel.Engine/SqlExtentions/PROFILING.md`).

The release build copies this folder into Office365ActivityImporter.zip, next to the compiled webjob: the *Copy automation scripts* step of `.github/workflows/build-packages.yml`, which `ci.yml` and `pr.yml` both use. Nothing is added by hand.

So the zip contains:
* SPOInsights.WebJob.Office365ActivityImport.exe
* Common.Entities.dll
* ..
* AutomationPS\ProfilingJobs\Aggregation_Status.ps1
* AutomationPS\ProfilingJobs\Database_Maintenance.ps1
* AutomationPS\ProfilingJobs\Weekly.ps1

The installer unzips the package, finds the three scripts there (`ProfilingScriptsLocateTask`) and uploads them to the Automation account as runbooks (`RunbookUploadTask`).
