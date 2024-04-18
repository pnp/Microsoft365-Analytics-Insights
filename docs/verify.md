# Verifying the Deployment

Once deployed, these steps will ensure everything is correctly configured and healthy. There is a section on troubleshooting common problems at the end.

## Verify & Limit Access to Solution Website

One component installed as part of the solution is the administration website. It’s used to check the basic status of the system and enable deep analytics for Teams if that’s wanted.

Go-to the root address of your app service (as seen from the portal):

If the runtime authentication settings are correct, you’ll be able to login to your app-service & see the status website:

![Graphical user interface, text, application, email Description automatically generated](media/9cdeb1b52c22b81c606a2037ea89ff43.png)

Next up: **limiting access to this website**.

Depending on your company directory policies, this website may be accessible to anyone with a valid login on the same directory. We recommend you limit access to this site to just specific users that administer the analytics solution.

![Graphical user interface, application Description automatically generated](media/3bf8f72b1632d315af715456887bdd7a.png)

Here you can set whether only specifically assigned users should have access to the web application or anyone in the organization. We recommend limiting the access to specific administrators:

### Verify Import Jobs are Running

We need to verify that data is being imported successfully into the SQL database from the two web-jobs.

1.  In the Azure Portal, under the App Service -\> “web-jobs” blade you should see two web-jobs “AppInsightsImporter” and “Office365ActivityImport”.
    1.  They should both say “running”.

        ![A screenshot of a computer Description automatically generated](media/2230ae4a14e079564ae2e1bf9c07f37f.png)

    2.  You can click “logs” to open a new tab/window & see the web-job logs to determine any errors.
    3.  Both web-jobs print their configured values at start-up, so verify these settings if there are any errors.

The AppInsightsImporter should look something like this on a good start-up:

![A screenshot of a computer Description automatically generated](media/f6e1ff049ff3aa6d0d6ad56cd83da18f.png)

The Office365ActivityImporter job should report something like this if it is working fine:

![A screenshot of a computer Description automatically generated](media/f43265d2ec0f50254647bc2560edee4b.png)

These logs are also available at “C:\\home\\data\\jobs\\continuous” on the app service if you want to look at the full log.

For more logging info, please check Application Insights trace logging– see more info in section 4.4.5.

At this point, the data-processing part of Office 365 Advanced Analytics Engine is setup!

### Monitor Performance

By default, the resources are created with minimal level performance tiers to avoid unexpected Azure consumption. It is necessary to monitor KPIs to ensure the PaaS components are sufficiently scaled.

Example of an under-scaled database:

![A screenshot of a computer Description automatically generated](media/508ef7a4644fe5cbae38f6e67b1a6080.png)

In this case we would recommend upscaling the database to the next performance tier.

Another KPI to monitor: the app service plan CPU & memory percentage.

![A screenshot of a computer Description automatically generated](media/84895f4935691b5b8aa1ff1ddaf0a175.png)

If you see CPU at 90-100% constantly, you may need to upscale the app-service.

## Monitor System Messages in Application Insights

All system logging is also registered in the Application Insights instance created for web-tracking.

![A screenshot of a computer Description automatically generated](media/1907aec15b88044fca753d7b8a78fcc2.png)

### Example Log Analytics Queries

“Office 365 importer” web-job messages (queries are multi-line):

traces

\| where operation_Name == "Office365ActivityImporter"

See when “Office 365 importer” has finished an import cycle:

traces

\| where operation_Name == "Office365ActivityImporter" and message == "Waiting 2 mins..."

For call-logging specifically:

traces

\| where operation_Name == "Office365ActivityImporter"

or operation_Name == "CallRecordWebhookController"

**Important**: this cycle/message should be no longer than once every 24 hours. Any longer & the backlog will be growing quicker than the importer is importing. If this happens, you need to upscale the database & app-service-plan (see 2.4.4).

“Application Insights importer” web-job messages:

traces

\| where operation_Name == "AppInsightsImporter"

Graph API Webhook messages

traces

\| where operation_Name == "CallRecordWebhookController"

Call records read from Service Bus

traces

\| where message contains "ServiceBus"

### General Exception Searching

If you want to see where an error may be logged but are unsure where, you can query for all exceptions being logged to get a start:

![Graphical user interface, text, application, email Description automatically generated](media/61c91192fd9007b29c7d80d8848fa403.png)

This gives you a good idea of what may be causing problems, although be careful; some exceptions are normal to see, depending on the circumstances.

For example, if audit data is loaded for external users then those users won’t be found when the web-job tries to load the user from your Azure AD and a “not found” exception will be logged. These errors need to be studied, but exception reporting can give a clue why a certain table isn’t being populated for example, if there’s a blocking issue.

### Web-Job Log Files

If Application Insights does not show data for some reason, web-jobs also log files to the standard web-job logs on the app-service:

![A screenshot of a computer Description automatically generated](media/36e05056efe5b20cdf75d644c4d2c2aa.png)

In Kudu, the app-service logs are available, including any output from either web-job.

Navigate to “D:\\home\\data\\jobs\\continuous” + name of web-job, to see the file-system log.

![A screenshot of a computer Description automatically generated](media/88463d7dbec51021b7480b9ba091fd14.png)

You can edit the file directly to see the contents or download to your local computer.

### Recommended: Setup Health Alerts

Once everything is running, we highly recommend creating alerts for the system to monitor health & be able to respond to problems, should they happen.

![Graphical user interface, text, application, email Description automatically generated](media/7a8a9fd2590b64b04b5bba4ef1ab4843.png)

If something like this happens, you need alerts to warn you before the imports stop working.

We’re going to monitor the following metrics & problems:

**Data flow health status**

-   No recent data from App Insights
-   No recent data from Activity API
-   Issue writing data in SQL

**Infrastructure KPIs for capacity monitoring**

-   App service plan
-   SQL Database
-   Redis

**Permissions**

-   Access denied errors:
    -   AAD App secret expiration
    -   Yammer access issue (given it’s tied to a service account)

This we can do with Application Insights alerts.

![Graphical user interface, text, application, email Description automatically generated](media/be6e23485e64855669288451111b64dd.png)

Rules allow us to be notified when specific events occur.

Example: create a rule to check if there’s no recent data into App Insights.

Why might this happen? A bad deploy of a new tracker version for example. It probably won’t happen, but it could for many reasons potentially; but we need to know if this happens ASAP so we can fix it.

Create a new rule & add a condition:

![Graphical user interface, text, application, email Description automatically generated](media/fb5af3d7916720cf102a3f9bc2a7c104.png)

The signal for the condition will be “custom log search” meaning, from the results of an Application Insights query.

For the query we just want “pageViews”, nothing else.

For the alert logic, in this case, we want to know when no hits are arriving “for X time”. That is obviously going to be enough time that it’s clear there’s a problem instead of just inactivity, so lets say “48 hours”.

If there’s been no hits at all in 48 hours on any sites, then the chances are there’s something wrong – you may have your own ideas what X may need to be, but in this example, our alert will look like this:

![Graphical user interface, application Description automatically generated](media/0f41222da0438d155ea8cf195b70421a.png)

Here we can see that when page-views are equal to 0 over the last 48 hours (2880 minutes), the rule will trigger. In this case we’ve had 6 hits over the last 48 hours, which has been the case each 6 times it’s been checked over the last 3 hours (the number of evaluations is shown to give you an idea of the evolution of the data over time, but in our case it’s a demo environment hence the low traffic).

Save the trigger.

Next up: define an action when the trigger is hit, which we do with “action groups”, which is basically a way configuring a collection of things to happen when a trigger is hit. You pre-save an action group & associate it to an alert.

![Graphical user interface, application Description automatically generated](media/85c5588cdba0e01e29900ff626adc979.png)

From an alert, this is now selectable.

![Graphical user interface, text, application, email Description automatically generated](media/e933c64ea4f98449cefa488fd83c38aa.png)

Now add the alert details…

![Graphical user interface, application Description automatically generated](media/21df4363295577aa9979bf7d034e21be.png)

…and create. It will now be active.

#### Alert Rules

To create alerts for the other rules, here are the triggers you need to create. All are based on the “custom log search” signal.

All assume that when the count of the results are 0, then something bad has happened and should fire the trigger.

-   No recent data from Activity API

| App Insights Query                                          | Time Period             | Trigger count |
|-------------------------------------------------------------|-------------------------|---------------|
| traces \| where message contains "Finished activity import" | 24 hours (1440 minutes) | 0             |

This is the message logged at the end of an activity import cycle, assuming no error. We should see at least one a day.

-   Issue writing data in SQL

| App Insights Query                                                            | Time Period | Trigger count |
|-------------------------------------------------------------------------------|-------------|---------------|
| exceptions \| where outerMessage contains "because the database is read-only" | 10 minutes  | \> 2          |
| exceptions \| where outerType == "System.Data.SqlClient.SqlException"         | 1 hour      | \> 10         |

This will give alerts for persistent SQL errors, and much sooner, any problems with database sizing.

**Infrastructure Load**

-   App service plan

For this, you need to add an alert to the “app service plan” of the “app service”.

![Graphical user interface, text, application Description automatically generated](media/1471873e8a7ab98ccc673d776f707968.png)

It’s normal to have high CPU, so set this trigger value high for a fairly long period.

-   SQL Database

Create the alert on the database & select “DTU percentage” for the signal (not DTUs used). DTUs are a simplified measure of CPU, memory, disk R/W all together. If the database is consistently at 100% then you need to scale up the number of DTUs available.

![Graphical user interface Description automatically generated with low confidence](media/3f66442f2fb4ce6a0c2c53de6fa71463.png)

DTUs explained - [Service tiers - DTU-based purchase model - Azure SQL Database \| Microsoft Docs](https://docs.microsoft.com/en-us/azure/azure-sql/database/service-tiers-dtu)

-   Azure Cache for Redis

Redis is unlikely to need scaling as it’s usage is fairly limited, but just in case, measure “Server Load (Instance Based)” and keep below 80%.

-   Service Bus

Service bus can need scaling if the number of calls to be tracked is very high. Check the “Throttled Requests” signal and make sure it stays at 0 always, as with “server errors”.

Other

-   AAD App secret expiration

| App Insights Query                                                                | Time Period | Trigger count |
|-----------------------------------------------------------------------------------|-------------|---------------|
| exceptions \| where innermostMessage contains "Invalid client secret is provided" | 10 minutes  | \> 2          |

-   Yammer access issue (given it’s tied to a service account)

| App Insights Query                                                | Time Period | Trigger count |
|-------------------------------------------------------------------|-------------|---------------|
| traces \| where message contains "No auth code found for Yammer"  | 10 minutes  | \> 2          |
| traces \| where message contains "Error requesting Yammer export" | 10          | \> 2          |

The 1st error occurs when there’s no code in Redis for Yammer, and the 2nd one when the code is invalid. Both should have alerts created for them.
