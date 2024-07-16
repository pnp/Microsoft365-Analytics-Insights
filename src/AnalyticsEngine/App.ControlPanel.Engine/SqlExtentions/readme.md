## Installer SqlExtentions
The installer will install every "*.sql" file in the "SqlExtentions" folder. 

**Important**: these files must be embedded resources in the installer project.

This is useful for adding custom SQL scripts to the installer, like the profiling extensions. The scripts will be run in alphabetical order.

This is done as part of the standard database upgrade process, either via the UI or command-line execution (usually by another instance of the installer actually executing the upgrade). 
The scripts are run after the database schema has been updated with Entity Framework migrations.

Note: SQL server doesn't normally support "```GO```" statements in stored procedures ("GO" is a SQL Server Management Studio thing to separate scripts in a single file), so you should avoid using them in your scripts here. 
You *can* use the "GO" statement and the installer will split each segment by "GO" and then execute each segment (bit of a hack), but you should put the scripts in separate files or remove "GO" from any script here.
