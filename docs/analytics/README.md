# Activity Analytics

## Description

Using the M365 Advanced Analytics database, this feature aims to allow an administrator to create profiles for different types users depending on their usage of the different M365 services used.

A new set of tables is created in the database that aggregate the data from the activity tables of the M365 Advanced Analytics engine. These tables are then used in a Power BI report that allows to analyze the users' activity.

Since the data available in the database/report shows aggregated usage and activity per user per week, it allows anyone, not only tenant admins to analyze this data and make decisions about adoption plans, license usage, etc.

### Available metrics and data

TBD

## Dependent Azure resources

The resources that automate the weekly aggregation of the data used by the Power BI report are:

* Automation Account: used to run scheduled PowerShell runbooks.
* Runbooks: several runbooks will be created for different tasks.

    Name | Description | Schedule
    -|-|-
    Weekly|Aggregates data for the report|Sunday 6pm
    Database_Maintenance|Keeps SQL indexes and statistics updated|Sunday 1pm and Sunday 11pm
    Aggregation_Status|Outputs table sizes and row counts.|On-demand (manual)

    These runbooks are PowerShell scripts that execute SQL stored procedures, so all the work is done at the SQL level. Runbooks are only used to schedule their run.

For this extra reporting to work, the automation jobs to compile the data all need to be enabled.

By default, if usage imports are enabled in the installer, the automation resources to compile these statistics are created but not scheduled, so they won’t run. This is due to the extra demand for the SQL Server database that this extra processing requires.

### Where are the PowerShell scripts stored?

As part of the installation, the PowerShell scripts needed are taken from the solution zip files, copied into your storage blob container (private access):

![Runbooks in blob storage](../media/analytics-runbooks-in-storage-account.png)

Shared Access Signature URLs are generated from uploaded PS files to give read-only access to these files and these URLs are what the automation account uses to download and run the scripts when the runbooks are created. In any case, the runbooks do not contain any installation specific data.

### How much extra resources are needed?

**Recommended performance**: minimum 50 DTUs for small organizations (up-to 20k users) for SQL Database, 100-200 DTUs for larger organizations.

**Space**: approximately 8gb per 10 thousand users, for 1 year of retention.

You can control retention with the `WeeksToKeep` variable:

![WeeksToKeep variable](../media/e51528c250ee44076846270e97293fc3.png)

This variable is read by the maintenance script.

### Automation variables

There are some variables in the automation account:

Variable|Type|Value|Description
-|-|-|-
SqlDatabase|String|database|Name of the analytics database
SqlServer|String|server.database.windows.net|URL of the database server
WeeksToKeep|Integer|52|Retention in weeks of the analytics data. Data is cleaned every week

### Automation credentials

These are the credentials used by the runbooks to access the database.

Name|Username|Password
-|-|-
SQLCredential|*sqladmin (default)*|*SQL password*


## Installation

Once the installation of the engine is completed follow these steps to set up the analytics reports:

1. Confirm that there is data available to aggregate.
2. Link runbooks to schedules.
3. Wait for the runbooks to run.
4. Connect and publish the Power BI reports.

### 1. Confirm data availability

Open the Automation Account and click on the `Aggregation_Status` runbook.

![](../media/analytics-status-runbook.png)

Start it from the toolbar:

![](../media/analytics-status-start.png)

Wait for it to finish and review the Output tab:

![](../media/analytics-status-output.png)

If you see data in the dbo.* tables, you can move forward.

### 2. Runbook schedules

Schedules are pre-created in the automation account; they just need linking to the right runbooks. Link the schedules by going to the Automation account in Azure portal.

![Runbook list](../media/ebc34465ff6c74abb58f6836304e0479.png)

**Create Schedules for “Database Maintenance”**

Find the runbooks and click on `Database_Maintenance`.

![Link to schedule location](../media/62a1fad26c4ecc80f0bd72c43a1073be.png)

Link the runbook to a schedule – we’ll add this runbook to two schedules, one by one. First one is `activitylog` maintenance, every `Sunday at 1pm`:

![Schedule choosen](../media/6d16af070d721bf7b1a688df4148b68c.png)

Now set the parameter to start. Click “configure parameters and run settings” and enter `activitylog` in the MAINTENANCETYPE parameter box:

![MAINTENANCETYPE parameter](../media/9721800dae1f41515e90d9cf1c74a1b3.png)

Click OK to confirm parameters. Click OK to confirm this schedule.

Now link another schedule for this runbook to create one for weekly DB maintenance:
- Schedule: `Weekly Sunday 11pm`
- Parameters:
    - MAINTENANCETYPE = `weekly`

![Schedule configured](../media/80ce070624fd7ca7fcea1854f1870724.png)

Click OK to save.

Confirm you see both schedules for the maintenance runbook:

![Maintenance runbook with double schedule](../media/6000ef545dd258d09a699608e99a6620.png)

**Create Schedule for “Weekly Update”**

Link a new schedule for the runbook `Weekly_Update`:
- Schedule: `Weekly Sunday 6pm`
- No parameters

![Runbook scheduled](../media/612fa10fdd64f90a89fffc4c181ab329.png)

Both these jobs should run without errors but should only be run out of peak hours.

### 3. Wait for runbooks to run

The aggregation runbook `Weekly` will run on the next Sunday. You can start it manually and wait for it to finish.

>Note: Sometimes the data to aggregate can be large and the runbook may timeout. Runbooks have a 3 hour maximum execution time. You can start the runbook and the work will continue where it stopped the last time.

Run again the `Aggregation_Status` runbook. If you see data in the profiling.* tables, you can move forward with the report.

![](../media/analytics-status-output-full.png)

### 4. Connect and publish the reports

TBD
