# Troubleshooting Dataflows & Setup

Here are some common issues & resolutions for getting Office 365 Advanced Analytics Engine setup.

## General Troubleshooting

Troubleshooting the system involves two steps:

1.  Figure out which imports are working/not-working by detecting a “finished” event for each one.
2.  For each import not finishing, what logs & errors are generated for it?

For step 2, the process is to disable everything except that failing import to get a clean set of traces and logs for when is fails.

There are several import flows in the system involved in any given import cycle:

-   AppInsightsImporter import channels:
    -   Web + events (from Application Insights).
-   Office365ActivityImporter import channels:
    -   Audit (from the Activity API).
    -   Graph import:
        -   User metadata
        -   Usage reports (all)
        -   Teams/channels

Each channel will report when it is successfully finished importing with a “FinishedSectionImport” custom event name in Application Insights, along with how long it took. When the importer has finished a whole cycle is a “FinishedImportCycle” event is logged; also with the total time taken.

![Graphical user interface, text, application Description automatically generated](media/e9a33ab21deda3d41daaf38e75484552.png)

Here we can see import cycles per process, and in each cycle the sections being confirmed as completed.

On a healthy environment, with all imports enabled you will see (in no particular order):

-   FinishedSectionImport - Hits import
    -   FinishedImportCycle – **AppInsightsImporter**
-   FinishedSectionImport - User Teams apps refresh
-   FinishedSectionImport - Usage reports
-   FinishedSectionImport - Teams import
-   FinishedSectionImport - Audit events
-   FinishedSectionImport - User metadata refresh
-   FinishedSectionImport - User Teams apps refresh
    -   FinishedImportCycle - **Office365ActivityImporter**

Check you can see these messages with this query in Application Insights:

customEvents \| where name == "FinishedSectionImport" or name == "FinishedImportCycle"

This will give you all section finished events from both importer jobs. If you just want the activity imports, run this:

customEvents \| where (name == "FinishedSectionImport" or name == "FinishedImportCycle") and operation_Name == "Office365ActivityImporter"

If you are missing any of those events within a 24-hour period and ImportJobSettings is set to import them, you should follow the below advice to start investigating why.

## Getting Problem Logs

If you suspect a problem with a particular flow the method to troubleshoot is the following:

1.  Disable all other imports via the “ImportJobSettings” setting (see below).
2.  Once configured, review exceptions logged for that single flow.
3.  Confirm you can/cannot see the “FinishedSectionImport” event is seen for the flow.
4.  Repeat the process with other import flows – verify each one can finish without fatal exceptions.
5.  Check on the app-service-plan free memory especially. If there is a lot of data the import processes can crash from running out of memory. It can also be worth increasing the scale of the app-service-plan to a much higher SKU temporarily to verify the problem isn’t around performance.

If you find a fatal exception that isn’t obvious what the cause is, please email with the details. We may need your configuration to be able to test and reproduce the error with a debugger.

## Teams, Calls, Events Data Issues

This data is all imported with the “Office365ActivityImporter” web-job. It imports data in the following order:

-   Teams
    -   User activity – usage & device reports
    -   Teams, channels: apps installed, members, etc, per day
    -   Deep analytics if enabled: channel chat metadata processing
        -   Keywords
        -   Sentiment score
        -   Languages spoken
-   Activity import
    -   Audit-log import for SharePoint, and other enabled workloads.

#### No Calls Data in Database

If you have no records in the “call_records” table, this could be for any number of reasons.

Call records are pushed from Graph into the solution via a webhook notification & a subscription, all of which should be created automatically. From the webhook, the call JSon is added to a service-bus queue & then picked-up by the importer web-job.

If call records aren’t being imported into SQL, you need to:

-   Verify the Graph webhook is available.
    -   Go-to the app-service webpage.
    -   Click the “test webhook with validation POST” link to simulate a web-hook call.

![A screenshot of a computer Description automatically generated](media/000d9e5555cb8e7df0302e49af02b235.png)

-   Verify the subscription for Graph calls is created.
    -   In Application Insights, log analytics: run this query:
        -   To see if the webhook subscription was created ok from the importer web-job:

            traces

            \| where operation_Name == "Office365ActivityImporter" and (message contains "Verifying call webhook subscription" or message contains "Updated subscription" or message contains "Created subscription")

        -   If you want to see webhook subscription failures:

            traces

            \| where operation_Name == "Office365ActivityImporter" and message contains "Couldn't create webhook"

-   To see if the webhook has been called (which will add them to the service-bus queue for calls):

    traces

    \| where operation_Name == "CallRecordWebhookController"

-   Check for service-bus processing messages.

    traces

    \| where message contains "ServiceBus"

This data will tell you where in the chain the call records are failing.

**Note**: it can take 15-20 minutes for a call webhook to trigger, after a call.

#### No Events Data in Database

Imports

#### “Office365ActivityImporter” Web-Job Restarts

Also seen in logs: “FATAL ERROR: No org URLs found in database!”.

Make sure there is at least one record in the “orgs_urls” table

![A computer screen shot of a computer screen Description automatically generated](media/489b68d58583c02828a8452bec7f118a.png)

Without anything here, all activity would be imported, so if the table is empty then the web-job aborts execution & hence this error.

#### No Logging in Application Insights

If you see no data in Application Insights for any of the importer web-jobs like this:

![A screenshot of a computer Description automatically generated](media/8f2d549e1057f511a921548d93a8bb95.png)

You need to check the app-service logs to figure out why.

![A screenshot of a computer Description automatically generated](media/e40b0633eca6612242651d7196532a35.png)

In this case, we’ve updated the system without migrating the database correctly to the new schema, so the data-layer (entity framework) can’t initialise the database correctly & crashes.

Misc Errors

#### Database Schema/Context Model Error

You may see this error:

*The model backing the 'SPOInsightsEntitiesContext' context has changed since the database was created.*

This is due to a mismatch between the solution binaries & the database; usually newer binaries running on an old schema.

The installer application can update the schema if needed & should be done when applying updates, with this option:

![A screenshot of a computer Description automatically generated](media/ae95f41e7dde7d06e09b014717d43201.png)

This option will use the installer in the software sources (not the installer running the install) to initialise & upgrade the database schema. The installer calls a copy of itself.

The reason we use the “downloaded” installer is so we can be reasonably sure the installer has the same context model version as the web-jobs being uploaded & installed, so there will not be any mismatches.

**Forcing Schema Updates**

If you want to forcibly update a database to the same data-model as the installer itself, you can do so via this menu option:

![A screenshot of a computer Description automatically generated](media/107ecb1678dc59fff05f8f7dd9024ebf.png)

This window will perform the same operation as the proxy install, but in this installer instance.

![A screenshot of a computer Description automatically generated](media/0f80060d976e38571a5816fdf7120e3a.png)

**Important**: only do this if you are absolutely certain the installer you are running is the right build.

Enabling/Disabling Imports

The system can be set to only import certain data with a “ImportJobSettings” app-service configuration setting:

**Calls**=True;**GraphUsersMetadata**=True;**GraphUserApps**=True;**GraphUsageReports**=True;**GraphTeams**=True;**ActivityLog**=True;**WebTraffic**=True

Setting any category as “false” will mean that part is skipped.

These options are also set on the installer:

![Graphical user interface, text, application, email Description automatically generated](media/a23aa0f65047ec40d91a5b8e92507e20.png)

If you want to change them later, you can change them in the app service configuration:

![Graphical user interface, text, application, email Description automatically generated](media/e5c08fed1fd9d6d4a5e6995090e8034f.png)

Save configuration, and the next import cycle will skip whatever’s not enabled.

## Updating the Solution

Occasionally it’s necessary to update the solution to a new version; either to resolve an issue or to take advantage of new capabilities.

The new version can either be a stable build or a testing build if you need a fix more urgently.

### Installer Upgrade Method

For stable builds, just follow the installation instructions again making sure that the “latest stable” release option is selected in the sources tab:

![Graphical user interface, text, application Description automatically generated](media/f7fee2a85fc7340e7f977b4e7c82cb21.png)

For testing/dev builds you need to download each component manually from the releases website, and then in the installer specify each file location:

![Graphical user interface, text, application Description automatically generated](media/7f8a11899b0c806f07999f09806a4bd0.png)

Make sure you don’t mismatch each file.

Start the installation as before from the same configuration file + the above settings. The process will be as before:

1.  Create Azure resources where necessary (if this is an upgrade, it shouldn’t need to create anything new).
2.  Update the import web-jobs with the configured versions above.
3.  Upgrade the SQL schema with any new changes.
    1.  Most new versions don’t introduce schema changes, but some do.
4.  Save the configuration applied in the SQL database & enter SharePoint URLs installed to, to the “org_urls” table so activity imports work there too.
5.  Restart the app service.

The output should look something like this:

![Text Description automatically generated](media/bf97ae99a474dd839c890328315084a3.png)

**Important**: once done, verify the web-jobs are running as per section 4.4.2. If a database schema change has been applied or is required, then you should double-check the new binaries are running correctly.

See section 6.5 for more information.

## Network Restrictions for FTPS & SQL Outbound Ports

Some customers may have firewall restrictions that limit outbound traffic. For the installer, we need specific ports to be able to install or update the contents of the database & app-service.

We have built a test functionality to help clarify if the right ports are open:

![Graphical user interface, text, application, email Description automatically generated](media/10c4f7f4ee26fd9fd2e6ee8c74712c43.png)

You can test FTPS and SQL outbound in the installer using either an example FTPS & SQL endpoint we’ve provisioned for these tests, or after creating your own App Service & SQL server, use those endpoints for the “test configuration”.

This is useful if your security teams require the outbound rule to be for a specific endpoint and you want to verify with the test configuration button.

![Graphical user interface, text, application, email Description automatically generated](media/ab9bb0fa005f204f0a43e0382fdea4ec.png)

Here we’re automatically detecting the FTP host used in our app-service that’s been created and configured in the installer (either created already by the installer, or manually). These endpoints can then be used for the tests.

![Graphical user interface, text, application Description automatically generated](media/66817280af974d1f3273238b4bf5cbe2.png)

If your networking team require outbound ports open to a specific endpoint and you wish to use the test functionality, this is how you can change the test configuration.

## Optional: Enable Private Endpoints

If you need to ensure that all endpoints are private, you can integrate with a virtual network and restrict access to each resource via the same VNet.

Example network:

The resources in Azure should be accessible only via private IP addresses, accessible optionally via another private, on-premises network.

### Limitations

In this mode, the system does have some limitations around data collection and configuration.

-   The installer does not take into account private endpoint resources, so once an installation is converted to private endpoints, manual upgrades are needed if version updates are deployed. The manual process for installation and upgrade is documented.
-   Also, due to how calls are tracked with Graph pushing notifications to the app service, this won’t be possible with privately protected endpoint.

The lack of calls can be mitigated if the only app service has a public endpoint, but assuming that too is private, it’s impossible to receive [webhook notifications from Graph API for calls](https://learn.microsoft.com/en-us/graph/api/resources/callrecords-callrecord?view=graph-rest-1.0) and therefore we can’t log call data.

### VNet Configuration

For these steps to work, we need a virtual network (VNet) pre-configured with x2 subnets – a default one and then one for app services.

![A screenshot of a phone Description automatically generated](media/fd9a60e1de9434d1ffbc4f246aedc535.png)

All the dependencies will use the default subnet, and the app service will integrate with a second one.

Private endpoints are created on the VNet and add DNS zones for each resource as needed, so that each service has a private, internal IP address.

**Example**: for App Insights, when we create the endpoint, we get a new network interface card for it with a private IP address (10.0.0.X) + a DNS “westeurope-5.in.ai.privatelink.monitor.azure.com” (same as the public address).

![A blue screen with white text Description automatically generated](media/0bff62c53b172e2428ae5155d5c45506.png)

In this case, the browser will now route requests to the private address, will pass internally to the App Insights instance and therefore be accepted.

In our example we’re using Azure DNS, so resource name registration happens automatically.

![A screenshot of a computer Description automatically generated](media/08e1cba3154f647c8d3aae4d0d80f425.png)

Your own infrastructure may have different requirements.

**Important**: if you have your own DNS infrastructure to integrate with, please add verification steps for each Azure service added below.

### Configure Services for Private Access Only

In this guide, we assume that you have a working system created by either the installer or the manual configuration method.

#### App Service

Add a private endpoint so access to it is restricted. In the “networking” blade of the app service:

![A screenshot of a computer Description automatically generated](media/386c34ecf2b4c5a779c7b34f1cad27f3.png)

<https://learn.microsoft.com/en-us/azure/private-link/private-endpoint-overview>

Next, we need to integrate the app service into our VNet so the web-jobs can access private resources. Back in the networking blade click “VNet integration”

![A screenshot of a computer Description automatically generated](media/9cb767127f3c9fa89d283b008a4f23d7.png)

![A screenshot of a computer Description automatically generated](media/77fccfb9e68378429e5ccc98b7bed8b4.png)

Now, any HTTP calls from outside the private network will not arrive at the app service.

#### SQL Server

Restrict public access and enable a private endpoint.

![A screenshot of a computer Description automatically generated](media/4389c9a37b28bcae22fe22b76315a0ee.png)

![A screenshot of a computer Description automatically generated](media/02dafeaae15ca6ddcf75b9f67d63a108.png)

This will create a network interface for the private endpoint:

![A screenshot of a computer Description automatically generated](media/28694fb44b308ef9699b810f5a689990.png)

Select the same VNet.

![A screenshot of a computer Description automatically generated](media/229f9e5734623cd64dd2c0accc2e822d.png)

You may wish to create a specific IP address. We’re going for automatic configuration.

![A screenshot of a computer Description automatically generated](media/97a129ddc6454692c1f6967b36013cd4.png)

Now, apps need to use this internal address to connect to any database on this server.

#### Redis Cache

A similar setup with Redis is needed.

https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-private-link

![A screenshot of a computer Description automatically generated](media/eacc948bee6f095ebdba5c134f4fce0d.png)

![A screenshot of a computer Description automatically generated](media/cfa2124a638823a0b7cbdf632c394ecb.png)

![A screenshot of a computer Description automatically generated](media/28d8ea01a58ea6c69369f196e5c330d3.png)

#### Service Bus

Private endpoints and network isolation require a Premium tier service-bus. The installer creates a basic tier bus, so this needs to be upgraded first – via a “standard tier” level too.

![A screenshot of a computer Description automatically generated](media/b74342fd0705e1205f36cbdc382d16fb.png)

The service bus created by default is a basic tier one, so we need to migrate through the standard tier too:

![Screens screenshot of a screenshot of a screenshot Description automatically generated](media/c12dd74741921a497996ecfc82b91b56.png)

…meaning we need to create a standard tier namespace, and also a premium one.

![A yellow and purple text Description automatically generated](media/42ebc085cf30780a68512de3e8a7ed08.png)

![A screenshot of a computer Description automatically generated](media/334c7d5e10e79aaf6e9477d014890122.png)

Once your options are configured, you can start the migration from basic to premium.

![A screenshot of a computer screen Description automatically generated](media/81435a5113c0bbcb4ce8ba2badbd2789.png)

Once done we can create private endpoints as before:

![A screenshot of a computer Description automatically generated](media/36ade022ff80e74cacd24341614bdef5.png)

As before this will create a network interface in the selected VNet + register with DNS if managed by Azure.

#### Storage Account

For storage, we need to configure private endpoints for table + blob services.

![A screenshot of a computer Description automatically generated](media/16b93a66e7496ec6c43bb3123206a22f.png)

Enable private endpoints for blob & table storage.

![A screenshot of a computer Description automatically generated](media/082a87aebadc911f2e8529dcecafdea8.png)

Ensure the right sub-resource is selected.

![A screenshot of a computer Description automatically generated](media/fdda90e4be3a0a4fdf9adb70c104e309.png)

DNS configuration is done if managed by Azure. Manual configuration will be needed if DNS is handled by your own infrastructure.

![A screenshot of a computer Description automatically generated](media/9184ec98cb471a085655c31348f80e06.png)

Repeat the same for blob storage.

#### Optional: Language Service

Language service is optionally used for Teams channel sentiment analysis and other areas of M365. If your configuration doesn’t include this, you don’t need to follow this step.

![A screenshot of a computer Description automatically generated](media/a0f5da1f809e9719e844ac36ca2a1aa0.png)

Disable public access and save changes.

![A screenshot of a computer Description automatically generated](media/edaac4ab33006df48f91ed2e2b4be676.png)

There’s only one sub-resource:

![A screenshot of a computer Description automatically generated](media/1e418b62b0dbb8cf0de0bd86834ff8e8.png)

Configure how you want the endpoint to appear on the VNet:

![A screenshot of a computer Description automatically generated](media/cd43491ef183d0fe399ff593b34705f5.png)

Specify DNS

![A screenshot of a computer Description automatically generated](media/2b1411edc994ad496654dac32abf52b6.png)

No changes should be needed for configuration thanks to the CNAME above mapping the public DNS to the private DNS.

#### Application Insights

Access to App Insights ingestion and tracking is limited via “Azure Monitor Private Link Scopes” on the “Network Isolation” blade.

![A screenshot of a web page Description automatically generated](media/96399f7dae0d3368b80bc596e7d8d819.png)

Create new:

![A screenshot of a computer Description automatically generated](media/c473295c346ed5450d9d62dda911ae4d.png)

Add a network interface for private endpoint scope: ![A screenshot of a computer Description automatically generated](media/6ee8f4bdb6d3815651ac84efec0dcd8e.png)

![A screenshot of a computer Description automatically generated](media/e4da6065a123a920e985edcdee0dc835.png)

Configure DNS. There are a few services that App Insights need to have DNS for:

![A screenshot of a computer Description automatically generated](media/3471b7b713b11478886a56d5bbdbad4e.png)

One the scope & endpoint are added, you can configure Application Insights to use them.

![A screenshot of a computer Description automatically generated](media/aa87aa75d67057daab9f3ced04f7a511.png)

With public-network ingestion disabled, any attempts to log data will be denied. Here’s an example response:

![A computer screen shot of a program code Description automatically generated](media/0a268db745572468b005d8c9b1673035.png)

Equally with reading the data:

![A close-up of a document Description automatically generated](media/fc78eaec98ede32d9b0d772daffe6867.png)

You can only now read/write telemetry data from a device on the same VNet.

#### Log Analytics

Configure the workspace in the same way as Application Insights.

![A screenshot of a computer Description automatically generated](media/5440de9e91b615aea4bd7b573e5f2532.png)

Important: if you don’t configure log analytics with the same Azure Monitor Private Link Scope, no telemetry data will be readable.

### Configure App Services

Once components have been configured, the app service configuration will need to be updated.

#### Disable Call Imports

As public endpoints are needed for webhooks and this is precisely what we’ve disabled, we should stop the solution from even trying to setup the webhook as it will fail and generate errors.

In the app-services configuration, disable the calls import:

![A screenshot of a computer Description automatically generated](media/f09694c6794e8cde72f499c3182ddc17.png)

Save changes.

#### Change SQL Connection

Changing the SQL server connection will start the web-jobs to crash as they’ll no longer be able to access SQL from the previous DNS with this error:

SqlException: Reason: An instance-specific error occurred while establishing a connection to SQL Server. Connection was denied since Deny Public Network Access is set to Yes. To connect to this server, use the Private Endpoint from inside your virtual network.

In the app services configuration, change the SQL connection DNS:

![A screenshot of a computer Description automatically generated](media/b5d61828bf9a7bfa5a0678cfbc685c21.png)

Check app-service web job logs from a virtual machine on same VNet:

![A screenshot of a computer Description automatically generated](media/677124f91f0e800397937bdba558dde7.png)

Verify web-jobs are running:

![A screenshot of a web jobs application Description automatically generated](media/d44a6591b92d51e9020270aafd3d12f2.png)

Both web-jobs should be in the “running” state, without restarts.

#### Redis

Redis doesn’t require a change as a CNAME record is added so, in my example, “advanalyticsdev.redis.cache.windows.net” points to “advanalyticsdev.privatelink.redis.cache.windows.net”, which is pointed at the local VNet IP address.

This means that no changes should be needed for the app-service to work with the old, public DNS.

#### Service Bus

Equally the service-bus DNS should still work for the same reasons.

### Power BI Service (Optional)

The Power BI service will need to access the private endpoint for SQL if you’re using PBI reports - <https://learn.microsoft.com/en-us/power-bi/enterprise/service-security-private-links>

## Optional: Configure Runtime Account for Certificate Authentication

You can run the system with certificates instead of client secrets. Here’s how to set that up.

By default, no permissions are given for certificates in the key vault for any users – just service-accounts:

![A screenshot of a computer Description automatically generated](media/f112e4c68fbfc3a540b7f5e2198f467a.png)

We need to add permissions for the current user in Azure portal & the application identity for certificates (note: the installer now grants the runtime account “get” rights for certificates).

Grant User Access to Key Vault

Go to vault access policies.

![A screenshot of a computer Description automatically generated](media/202a92b46aac0ab4e913d6273880e8cd.png)

Create a new policy for the current user to manage certificates:

![A screenshot of a computer Description automatically generated](media/b7d2b70e267d6d3006e477a8e8f90cff.png)

It’s up to you what permissions you give yourself; your user permissions aren’t used by the import web-jobs. Your permissions should include though the ability to upload/generate certificates.

![A screenshot of a computer Description automatically generated](media/04e154910911c835de98c9cb5c0bbaa6.png)

These are for your user only.

Grant Runtime Account Read Permissions

In case you haven’t got the permissions configured already, allow the runtime account read access to certificates.

![A screenshot of a computer Description automatically generated](media/202a92b46aac0ab4e913d6273880e8cd.png)

We assume vault access policies are used.

![A screenshot of a computer Description automatically generated](media/0b9f35ad5dcc63e6ca723789ef6f5c6b.png)

Make sure “get” is selected:

![A screenshot of a document Description automatically generated](media/ac959e245439f30a6960f6dfc081b1bf.png)

Click “Next” and “save” to apply the permission changes.

Generate Certificate to Authenticate With

Create a new certificate to use for authentication. You can have Key Vault generate it for you, or use your own CA if you prefer that way.

Recommended: give it a long validity as access to APIs will break when it expires until the configuration in the app service is manually updated with the new certificate.

![A screenshot of a computer Description automatically generated](media/0ccb2b770aee9bc9f079f6d8a62894d5.png)

**Important**: the subject-name and certificate name must be “**O365AdvancedAnalytics**” (CN=O365AdvancedAnalytics) as that’s what the solution expects. For now, this isn’t configurable.

Also note: the private key for the generated certificate is stored as a managed secret, so the app service identity will need secret get permission too.

Now open and download the public key:

![A screenshot of a computer Description automatically generated](media/1565c8a8df900ba6f1519fcb1d9b7135.png)

Take note of thumbprint:

![A screenshot of a computer Description automatically generated](media/47261e4c7d9a807757d7de184872a736.png)

Upload Public Key to Entra ID (Azure AD) Application

The corresponding public-key can now be used for the application registration. In “certificates” for the application registration:

![A close-up of a document Description automatically generated](media/b0075e6a6b0efd5b8a7860221329d7d9.png)

Verify thumbprint is the same as certificate stored in Key Vault.

How to Enable Certificate Authentication

By default the system will use client-secret authentication. To force certificate authentication, add a setting “UseClientCertificate” with value “true” to the app service configuration and save. You can optionally remove the “ClientSecret” value too; with the 1st setting as “true”, the secret is ignored.

Another mandatory setting to have is “WEBSITE_LOAD_USER_PROFILE” with value “1” so the app service can parse X509 certificates. [More information online](https://learn.microsoft.com/en-us/azure/app-service/reference-app-settings?tabs=kudu%2Cdotnet#build-automation).

Recommended: Verify Web-Job Logs

Make sure the certificate is read successfully by checking the logs of the “Office365ActivityImport” job:

![A screenshot of a computer Description automatically generated](media/1fc35794f0983e429109111cfb6cb13f.png)

Here we see that the operation has failed because there’s no certificate in the key vault.

Certificate Rotation

There is no automatic certificate rotation support. When the certificate is renewed in Key Vault, you must overwrite the certificate in Entra ID with the new one too and restart the web-jobs.

