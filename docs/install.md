# Deployment Guidance

The following sections document the steps required to deploy the Office 365 Advanced Analytics Engine solution components.

The deployment can be done automatically or manually - but installation is highly recommended to be done with the installer/control-panel application if possible.

First, go ahead and read the [prerequisites page](prerequisites.md). Then come back here once you have understood them.

## Getting the installer

Solution assets are available via the downloads site - <https://m365advancedanalytics.azurewebsites.net>

This site contains the latest stable & testing builds from the DevOps repository pipelines.

The primary package you will need is the installer/control-panel application which is what installs the solution. The installer will install or update an install but can also test the readiness of the configuration loaded without making any changes.

Copy the Office 365 Advanced Analytics Engine solution component build assets provided to a machine that complies with the installer machines prerequisites.

![Sources tab in the installer](media/installer-tab-sources.png) [](c94515385760cccd66433b8c62b92a63.png)

To deploy via the installer, you need only download that which will then download the other assets automatically. The installer is in `ControlPanelApp`; download, extract and run `AnalyticsInstaller.exe` (note: it's called "Control Panel" as it was originally intended to do more than just install the solution).

If you are deploying manually or you're installing a specific build via the installer, you will need to download the assets manually:

Package | Description
-|-
AITrackerInstaller.zip|JavaScript + PowerShell to insert into SPO sites to track web-usage.
AppInsightsImporter.zip|This is the web-job binaries to import data from Application Insights
Office365ActivityImporter.zip|This is the web-job binaries to import activity from Office 365.
Website.zip|The app service website contents including the Graph webhook endpoint for call tracking.

The installer can install a specific build if needed:

![Sources tab in the installer](media/installer-tab-sources-manual.png) [](5d1b762dd3a837412c460e9a1d32a35a.png)

Unless otherwise requested, we recommend installing the latest stable build.

### Configuration Settings

Before being able to run the installation process either manually or automatically, the following information is needed.

Entra ID | Description
-|-
Installation service principal credentials | The service principal used by the installer/control-panel to create Azure resources.
Run-time service principal credentials | The service principal used by the runtime web-job to import Office 365 activity & Graph data.

Azure | Description
-|-
Resource group name | The target resource group for all Azure resources created.
Azure location | Region to create resources in.
Subscription details | The ID + Name of the subscription to be used. Can be autodetected if installer account has permissions to read Azure subscriptions.
Tags | Some organizations need to create resources with tags.
Application Insights name | Name of resource to create.
App service & app service plan names | Names of App Service to create + plan. App Service name must be unique.
Azure cognitive services app (optional) | Endpoint + key
Automation Account name | Name of automation account to create.
Blob storage account name | Name of storage account to create. Must be unique.
Azure Cache for Redis | Connection string
Azure Service Bus | Connection string
SQL Server name, admin username and password | Configuration for the database server to create. Must be a unique name.
SQL Database name | Name of the database to create on the SQL Server.

SharePoint | Description
-|-
Site collection URLs | Where to deploy the tracking script & enable activity tracking. One URL per line.
App catalog URL | Site collection of app catalogue for tenant.
SharePoint credentials | Credentials of a user with site collection rights on each of the supplied site collections to install solution on.

## Configure solution components

There are two methods to deploy the solution: automatic via the control-panel application, and manually. If you are using the control-panel/installer automatic method (recommended), you need an installation service principal.

**Express version:**

If you just want a quick overview of the installation process:

1.  Register an application in Entra ID to install solution with.
2.  Register an application in Entra ID to read Office 365 activity data.
3.  Setup solution dependencies:
    1.  **Via the installer** – recommended as it automates many parts of the solution setup.
    2.  **Manually** – fallback for if the installer route isn't an option.
        1.  Create Azure resources & configure app service with corresponding keys.
        2.  Upload web-jobs.
        3.  (Optional) Install SharePoint extension application.
        4.  (Optional) Run PowerShell to staple AITracker.js to site collections.
4.  Verify success: data is flowing & web-jobs are running continuously.

**Pre-Flight Checklist**:

Before installing, please check the following is verified:

1.  Runtime service account is created with permissions added and approved (see "**Prerequisite Permissions**").
2.  Azure service providers enabled (see "**Prerequisite Azure & Office 365 Configuration**").
3.  Ports open from installer machine for FTPS & SQL (see "**Prerequisite Firewall Rules**").
4.  Office 365 reports anonymisation is **disabled** - <https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/miscellaneous/reports-show-anonymous-user-name>
5.  **Recommended**: run a "test configuration" from installer before attempting any installation:

![Test Configuration button higlighted](media/installer-test-configuration.png)

When no errors are seen here, there's a good chance the installer will work. Any errors should be reviewed before installing.

### Prepare for deployment

As described above, we need x2 service accounts – an installation account for installing the solution, and a runtime application to import data from Office 365.

For both accounts follow this process:

#### Create Runtime Application

Create an app registration with Entra ID. Find the Entra ID section:

![AAD in Azure Portal](media/a196d0f6ee3d1e7b6059fd8f7eeed03b.png)

Add new app registration through portal.azure.com – the Entra ID blade.

![New app registration](media/bc5eb45ced4963ceeaba9a2286c93249.png)

New registration name: `O365 Advanced Analytics - Runtime` or `O365 Advanced Analytics - Installer` (you need to repeat this process for each registration).

![Create app registration. 1st step](media/de474e2c0347b2adcd1d13efde18f76e.png)

Leave the "redirect URIs" for now, but we will need to add them later for the runtime application. Click "Register" to create the application registration.

You should now be sent to the new application you've just created. Next, we need to add a client secret.

![Adding a new secret](media/9aad9f576049483a0e45e4a0f882f53a.png)

Create a new client secret for the app registration with the following information:

- **Description**: whatever you want. Blank is fine.
- **Expires**: the maximum amount you can (or a date you'll have to change the secret & update configuration).

Click "add" and the secret will be generated. Copy & store the key that is shown.

![Add button](media/6cbc8ab4643a9a5f8a91d0e197744030.png)

Remember to save the secret `value` – we will need this later. Do NOT copy the `Key ID`.

![Copy the secret value](media/c0110e416f66b6f5f9187442e90950ff.png)

Go to application overview. Copy & store application registration ID and the directory ID:

![App registration details](media/7aaf21b910480d7501ed2c69c6622238.png)

The application should be now created. You need the client ID, secret value, and directory ID for **both** installer & runtime applications.

>**Important**: both accounts need to be in the same Entra ID directory.

#### Grant permissions to the Office 365 Management and Graph APIs

The runtime service principal must have permissions granted to the activity and Graph API. This is not something granted by default, so needs to be added after registration.

The list of permissions can be found in the [prerequisites docs](prerequisites.md#prerequisite-permissions).

In the runtime app registration, add the permissions in the following screenshot. Click "API permissions" and then "Add a permission".

There are two sources we need to read data from: `Office 365 Management API` and `Microsoft Graph`.

The permissions for the Office runtime application should look like this if you need all the permissions used (see your solution specific documentation):

![API permissions](media/f98aa6209b3127c8ea5ba493237dcd0d.png)

When permissions are added initially, they are not granted until an Entra ID administrator can consent to them.

>**Important:** grant admin consent to the application to complete configuration.

![Admin consent button](media/7898765c0ac76cd76debe89f696e2630.png)

This may take some time internally within your organisation to get approved.

All Entra ID permissions should now be configured for the runtime application.

#### Testing Runtime application

Once the permissions above have been set, you can validate them in the installer application easily. Open the installer/control-panel, and just fill out these fields:

![Credentials tab in the installer](media/installer-credentials-runtime.png)

Then on the **Install** tab, click "Test Configuration"

![Test results shown](media/installer-test-configuration-apis.png)

When testing configuration, no changes are made in any way to anything. The installer just simulates similar reads the importer web-jobs will do and reports their success.

#### Add Platform configurations to Runtime application

For the web application to correctly authenticate users, its URL must be set up in the runtime application.

>**Note**: this authentication configuration step requires you know the URL of the Azure App Service you have or will-have created. You may need to come back to this step once the app service is created. Please don't forget though, as this is required for the website to work.

1.  In the app registration, add a new platform configuration by clicking in Authentication and then "Add a platform".
2.  Choose "Web application" and add the URL of the app service as redirect URL.

    ![Configure platforms](media/4738a3424f2ed66462c948e4cab51ae2.png)

    Add your web application URL as seen in the configuration wizard:

    ![App Service URL in the installer](media/installer-app-service-url.png)

    Once the web-app is created (so you know the name is valid), copy the root URL into the application registration configuration.

    ![Configure Web platform in app registration panel](media/d4537de61acf4ead0c0f31c0ec350be4.png)

    This redirect URL needs to be the root of your app service, HTTPS.

3.  Click on "Configure" to add the configuration.

#### Create Installation application

Follow the same process above to create a runtime service principal. For this account, no permissions are added, we just need an application registration & secret.

#### Create Azure resource group

For the installer we need to grant it permissions to an existing resource group in the Azure subscription. From the portal, create a new resource group:

![Create a resource group](media/84f304f27c6a0433022a24ab42e7a340.png)

Once created we can add the installer permissions to it.

#### Grant Installer permissions

In the resource group, add the installer account to list of role assignments:

![Resource group IAM](media/443d2edc6a3bfea4c831a71f072545a9.png)

Next find the role `owner`.

![Add role assignment](media/e74bf35c5ecc768b45a850b3d3a5bf62.png)

Next pick who we want to add as "owner" to this group.

![Select the service principal as member](media/d3eb4bbb95a78e8d33065a54f12382f5.png)

Select members and search for the installer account. Selecting it will add it to the select list.

![Select button](media/ffa1743f58cd9c5580f36ae083df264e.png)

Confirm we have the right permissions:

![Double check everything](media/a0e008298a995e6cd93963836f60379c.png)

Click "review + assign" to verify one last time.

![Triple check again](media/b9b52036ed149c4c7de030a32a678027.png)

When you click this a 2nd time, Azure will add the role:

![Azure banner showing the status](media/1b1944a661d90d23bd12e602c2d7f0cc.png)

The installer account has been added to the resource group.

Next, we need to do the same for the subscription, but with `reader` permissions. Go to the subscription that hosts the resource group:

![Subscriptions list](media/1ee81dde4649fa9bb62ae37a4fa812ce.png)

In access control, add a new role:

![Subscription IAM](media/994347d77b8f86bb8493c0e9fa4bddcc.png)

This time add `reader` role:

![Assgn the role](media/bba8e04e4513742c1aa62ad96b2b2c13.png)

Great! The installer should now be able to create all the resources needed.

#### Enable Implicit Grant Flow for Runtime application

Implicit grant flow must be enabled so that users can login to the web application that's created for the solution.

>**Note**: we recommend doing this after the app service has been created as you'll need the app service URL.

First, we need to add "web" as a platform on the runtime app registration:

![Add a Redirect URI](media/81bcce83be776f9fb9c09cd74df089d2.png)

From the "authentication" blade, add "web":

![Add the Web platform](media/40df65730125736738fbb503ddf286d6.png)

Enter the base URL of your app service:

![Adding the app service as redirect URI](media/d3409c2eea8ab6f1c70c8b19061d7ed5.png)

Make sure that both `Access tokens` and `ID tokens` checkboxes are ticked.

Test the configuration by accessing the root URL of the web-app.

## Solution installation – Automatic Setup

>**Note**: Follow these steps using the installer application. For manual installation and creation of the required Azure resources, visit this page: [Install manually](install_manually.md)

The first step is to set up the data collection.

There is an installer application to help setup everything, but it needs permissions in Entra ID to do so. If it doesn't work because of permissions or any other reason, the manual installation is the fallback method & explained too.

>**Important**: The installer account needs to be in the same tenant as Office 365.

### Run the installer

Extract the control panel application & run `AnalyticsInstaller.exe`. You may be interrupted by Windows SmartScreen:

![Windows SmartScreen](media/857ae04a27035a8193c41f59be11085b.png)

Click "more info" and then "run anyway". If you don't have that option, you need to configure Windows Defender SmartScreen to only warn (the default Windows setting).

### Verify resource group permissions

All the Azure resources will be created in a single resource group. We recommend pre-creating this resource group and assigning `owner` rights to the installer account for this group only.

![Resource group IAM](media/06b91806b12c6881548ef4f0656c180b.png)

The installer can also create the resource group if the installer account has the right permissions on the subscription.

### Fill in installer fields

Next, we need to fill out the remaining configuration. Trying to save the configuration will tell you if there's any validation errors/missing data.

>**Note**: the screenshots in this documentation may not reflect the exact experience of the installer you see. The same information will be there however, just maybe differently presented.

#### Targets

Pick the data you want to import and/or the solution you wish to install. For Adoptify, the list of imports is preselected:

![Targets tab - Adoptify](media/installer-targets-adoptify.png)

See the Adoptify deployment guide for more information about setting up this solution specifically.

For Advanced Analytics & Insights, you can customise what data-sources you wish to import:

![Targets tab - Advanced Analytics](media/installer-targets-analytics.png)

Later, you can also reconfigure which sources to include or ignore.

#### Credentials

At this point you should have both service accounts filled out in the installer from the above configuration.

![Credentials tab](media/installer-credentials.png)

#### Azure Configuration

The resource group needs to be the same name as the group we created previously (or not if we wish the installer to try and create the group).

![Azure Config tab](media/installer-azure-config.png)

Make sure a subscription is selected. The refresh button will use the supplied installer account details to try and refresh this. If it fails, you can enter the details manually.

The performance tier configuration gives you a chance to configure an initial pricing tier for the environment. This only applies for resources created for the 1st time (if Azure resources are found already the performance settings are left untouched) and are the general recommendations for each type of environment.

Later, we highly recommend tuning the performance tier to your specific needs.

#### Azure PaaS

The following resources need unique names in Azure as they will by default have public endpoints, so public & unique DNS names are needed:

- Key vault
- Storage
- SQL Server
- Redis
- App Service name (the plan can be shared)
- Cognitive Services

Change these to something that's unlikely to exist already.

![Azure PaaS tab](media/installer-azure-paas.png)

#### Azure Storage

On this page, Azure PaaS storage related resources related can be configured. Again, names must be unique. You can use the default SQL server username if you'd like.

![Azure Storage tab](media/installer-azure-storage.png)

#### SharePoint (Advanced Analytics & Insights Only)

>**Note:** You only need to do this if you are planning on using the SPO Insights part of the solution.

This page needs the credentials for the SharePoint sites where the tracking code (AITracker) is going to be injected. This is a highly privileged process, so site collection admin credentials are needed.

Also, in this page is the list of site collections where AITracker is to be installed, and the app-catalogue URL to install the SharePoint Framework Extension package to.

>How to create an app-catalogue: <https://docs.microsoft.com/en-us/sharepoint/use-app-catalog>

![SharePoint tab](media/installer-spo-sites.png)

The "domain" field is taken from the Entra ID domain backing SharePoint & Office 365.

#### Install

When everything's filled out, you should be ready to install. You can specify what tasks are to be done by the installer, from complete install & validation to just updating the solution components if needed.

![Install tab](media/installer-install-tab.png)

Click "Install/Upgrade" button once ready.

If there are any validation errors, this is when you'll see them:

![Example error](media/b301d47ea83cb0be2c5a84fb31b55651.png)

When the installer has all the required data it will begin the process.

Assuming there are no validation errors, the installer settings are saved locally so you don't need to configure everything again.

**Handle Installer Errors**

If possible that errors will occur the first time at least; naming conflicts etc. These should be logged by the installer so you can make any configuration changes needed.

**Save Configuration file**

In order so you don't have to fill out everything again, you can save the configuration data as a JSon file for later. The files are encrypted (or at least the sensitive fields in the file are), so a password is needed to load & save configuration files:

![Password to save configuration file](media/5d304eebd47769941b4c1d60caecc976.png)

Once you are ready to go, click "install/upgrade" to apply your chosen configuration.

![Installation running](media/installer-installing.png)

You'll see the installer will create any resources that do not exist yet and will also run any actions specified above. If you're upgrading an existing solution, it's highly recommended you allow schema upgrades.

Any errors found will be logged & the installation can be run several times without any risk. If you do have any errors please verify the solution prerequisites are ready (see [prerequisites](prerequisites.md)).

## Additional solutions

Once the analytics engine has been installed, you can choose to enable some additional features listed here. These are optional and your requirements will vary depending on how you want to use the whole solution.

### Enable Deep Analytics for Teams

On the administration website is also where you can give analytics deeper access to Teams content; specifically channel chat.

**Important**: for enabled sites, chat content is read but never stored – just metadata for channel chat including:

-   Languages used
-   Sentiment analysis (positive vs negative).
-   Keywords.

In order to do this though, a user has to give analytics access to each Team proactively.

![](media/4fd6054ad5962d9385f10875b94eb9b0.png)

Note: the page can take a few seconds to load the 1st time.

Teams that have access given will be crawled next import cycle; usually 3-4 of hours later latest.

### Enable Access to Yammer

If you're running Yammer analytics, you need to grant access to Yammer data as an administrator so the importer can read anything.

Open the administration website and navigate to "/yammerauth" page.

![Yammer auth page](media/436a8eb606e0d6a16c618502e5e8a673.png)

You'll be asked to login and authorise the application.

Once successful, Yammer will redirect back to the analytics site with a code that'll be converted into an access key & stored in Redis for yammer crawling.

![Yammer code got got](media/179746c4dbe6209c0fbe23e60bcbe4f1.png)

The Yammer export uses the [export](https://developer.yammer.com/docs/data-export-api) endpoint to retrieve data.

### Enable Activity and Usage Analytics extensions

There is an extension where you can enable enhanced user activity analytics capabilities to the solution, so you can report on aggregate usage stats per demographic like "job title", "department" etc.

This gives the ability to split the data by demographic and see which workloads and Office 365 capabilities aren't being harnessed at a much more granular level than standard reports.

Enabling this extension will provision additional tables in the SQL database. There are two Power BI reports that use these extra tables to give you these statistics.

![Activity Analytics report screenshot](media/activity-analytics-report.png)

Review the instructions here to setup the analytics extensions: [Set up analytics](analytics/README.md)

## Further reading

- [Verify the deployment](verify.md)
- [Troubleshooting](troubleshooting.md)
- [Known issues](knownissues.md)
- [Database Schema](dbschema.md)
