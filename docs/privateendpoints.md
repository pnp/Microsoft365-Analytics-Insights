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

