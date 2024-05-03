#
# InstallSPOInsightsTracker.ps1, version April 2020.
#
# Installs AITracker.js into configured site-collections
# then adds custom-actions to all sub-sites with a reference to load it in the SharePoint sites.


Param(
	$ConfigFileName = "DevConfig.json", [switch] $UninstallOnly
)


function Get-ScriptDirectory
{
  	$Invocation = (Get-Variable MyInvocation -Scope 1).Value
  	Split-Path $Invocation.MyCommand.Path
}
$scriptPath = Get-ScriptDirectory
$spoCredentials = $null
$aiModernUITrackerDecription = "SPO Insights ModernUI AITracker App Customizer"
# ProcessScriptWithConfig($ConfigFileName) called at end of script

# Install in a specific site-collection
function InstallAITrackerToSiteCollection($siteCollectionRootWebUrl, $config)
{
    # Set vars
    $sourceFile = $siteCollectionRootWebUrl + $sourceFileRelative

	# authenticate
	$Context = New-Object Microsoft.SharePoint.Client.ClientContext($siteCollectionRootWebUrl)
	$spoCredentials = New-Object Microsoft.SharePoint.Client.SharePointOnlineCredentials($username, $password)
	$Context.Credentials = $spoCredentials


	# Load SPWeb data & objects
	$web = $Context.Web
	try
	{
		$allWebs = $Context.Web.Webs
		$Context.Load($web)
        $Context.Load($web.Lists)
        $Context.Load($web.UserCustomActions)
        $Context.Load($allWebs)
		$Context.ExecuteQuery()
	}
	catch
	{
		Write-Host "ERROR: $username doesn't seem to have the right permissions to $siteCollectionRootWebUrl ?" -ForegroundColor Red
		return
	}

	# Upload JS to library in site-collection

    # Do we need to create SPOInsights DocLib?
    $list = $web.Lists | where{$_.Title -eq $config.SPOInsightDocLibTitle}
    if($list)
    {
        Write-Host "Destination list for AITracker '" $config.SPOInsightDocLibTitle "' exists already. Won't create it then."
    }
    else
    {
        #Execute if list does not exists
        Write-Host "List " $config.SPOInsightDocLibTitle "does not exists. Let's create that..." -ForegroundColor Yellow

        # Create doc-lib for SPO Insights
        $lci = New-Object Microsoft.SharePoint.Client.ListCreationInformation
        $lci.title = $config.SPOInsightDocLibTitle
        $lci.description = "SPO Insight Asset Library"
        $lci.TemplateType = 101
        $list = $context.web.lists.add($lci)
        $Context.load($list)

        # Send the request containing all operations to the server
        try{
            $Context.executeQuery()
            write-host "Created $($listTitle)" -ForegroundColor Green
        }
        catch{
            write-host "Error creating $config.SPOInsightDocLibTitle - $($_.Exception.Message)" -ForegroundColor Red
			return
        }
    }

    $List = $Context.Web.Lists.GetByTitle($config.SPOInsightDocLibTitle)

	
	# Break security inheritance - https://docs.microsoft.com/en-us/previous-versions/office/sharepoint-csom/ee545470(v=office.15)
	$List.BreakRoleInheritance($false, $false)
	$List.Update()

	# Assign read-only permission to everyone
	$User = $web.EnsureUser("Everyone")
	$Context.Load($User)
	$Context.ExecuteQuery()
	
    $RoleDefinition = $web.RoleDefinitions.GetByName("Read") 
    $RoleAssignment = New-Object Microsoft.SharePoint.Client.RoleDefinitionBindingCollection($Context)
    $RoleAssignment.Add($RoleDefinition)  
    
    $List.RoleAssignments.Add($User,$RoleAssignment) 
    $List.Update()
    $Context.ExecuteQuery()

	# Read AITracker.js contents
	$aiTrackerFileName = ($scriptPath + "\AITracker.js")
	$FileStream = New-Object IO.FileStream($aiTrackerFileName,[System.IO.FileMode]::Open) -ErrorAction:Stop

	# Exit if source file couldn't be read
	if ($FileStream -eq $null) {
		Write-Host "Local AITracker.js couldn't be read!" -ForegroundColor Red
		return
	}

	# Check file doesn't exist already in site.
	#Write-Host Checking if $sourceFile exists...
	try
	{
		$existingFile = $Context.Web.GetFileByUrl($sourceFile)
		$Context.Load($existingFile)
		$Context.ExecuteQuery()

		Write-host Removing previous AITracker.js... -ForegroundColor Yellow
		$existingFile.DeleteObject()

	}
	catch [System.Exception]
	{
		Write-Host "AITracker.js doesn't exist for user $username! Let's upload it..."
	}

	# Upload AITracker to SharePoint
	# Create new file information for JS
	$FileCreationInfo = New-Object Microsoft.SharePoint.Client.FileCreationInformation
	$FileCreationInfo.Overwrite = $true
	$FileCreationInfo.ContentStream = $FileStream
	$FileCreationInfo.URL = $sourceFile

	# Try uploading. Will fail if another user has it checked out.
	try {
		$Upload = $List.RootFolder.Files.Add($FileCreationInfo)

		$Context.Load($Upload)
		$Context.ExecuteQuery()

		Write-Host "AITracker.js uploaded to $sourceFile" -ForegroundColor Green
	}
	catch
	{
		# Something went wrong; output error & stop execution.
		Write-Host "Error uploading the new file - see error below for why. Aborting operation." -ForegroundColor Red
		$error[0].ToString() + $error[0].InvocationInfo.PositionMessage
		return
	}

	# Check AITracker in if checked-out
    if ($Upload.CheckOutType -ne [Microsoft.SharePoint.Client.CheckOutType]::None){
        Write-Host File is checked-out. Checking in major version of AITracker.js...
        $Upload.CheckIn("InstallSPOInsightsTracker", [Microsoft.SharePoint.Client.CheckinType]::MajorCheckIn)
        $Context.ExecuteQuery()
    }
    else
    {
        Write-Host AITracker.js is already checked-in to destination library. No need to check in.
    }

	# Add custom-action for this web. Sub-sites done recursively.
    Write-Host Setting custom-action on all subsites to include AITracker.js in the HTML header...
    Write-Host
    AddAITrackerCustomActionToWeb $web $config
}

# Install in all sites listed in the config
function ProcessScriptWithConfig($configFileName)
{
    # Load config
    try
	{
        $config = Get-Content ($scriptPath + "\" + $configFileName) -Raw -ErrorAction Stop | ConvertFrom-Json
        Write-Host ("Read configuration for environment name '" + ($config.EnvironmentName) + "'...")
	}
	catch
	{
		Write-Host "FATAL ERROR: Cannot open config-file '$configFileName'" -ForegroundColor red -BackgroundColor Black
		return
	}

	# Set Params
	# Credentials need site-collection admin rights
	$username = $config.AdminUsername
	$password = ""

	# App Insights Key
	if ($config.ApplicationInsightsConnectionString -eq $null) {
		Write-Host "FATAL ERROR: No property value with name 'ApplicationInsightsConnectionString' in '$configFileName'. Is this config file an old one? We use Application Insights connection-strings instead of just instrumentation keys since Sep 2023" -ForegroundColor red -BackgroundColor Black
		return
	}
	$appInsightsConnectionStringBytes = [System.Text.Encoding]::UTF8.GetBytes($config.ApplicationInsightsConnectionString)
	$appInsightsConnectionStringEncoded = [Convert]::ToBase64String($appInsightsConnectionStringBytes)

	# Destination params
	$sourceFileRelative = $config.SourceFileRelativeDestination

	$targetFolder = $config.TargetFolderName

	# Load SPO Online Assemblies
    $loadInfo1 = [System.Reflection.Assembly]::LoadWithPartialName("Microsoft.SharePoint.Client")
    $loadInfo2 = [System.Reflection.Assembly]::LoadWithPartialName("Microsoft.SharePoint.Client.Runtime")

    # Read password in from secure-string
    $SecureStringFile = ".\" + $ConfigFileName + "-SecureString.txt.tmp"

    # Try to decrypt password
    try {
        $password = cat $SecureStringFile -ErrorAction:Stop | ConvertTo-SecureString -ErrorAction:Stop

        # password reading seemed to work; continue
        Write-Host "Read encrypted password from $SecureStringFile. Installing AITracker.js..." -ForegroundColor Green
    }
    catch [System.Exception]
    {
        Write-Host "ERROR: Problem reading password cipher from $SecureStringFile. Please enter the password for $username in a sec..." -ForegroundColor Yellow

		# Refresh password file
        RefreshSecureStringFile

		return
    }

	# Output what we're going to do
	if ($UninstallOnly.IsPresent -ne $true)
	{
		Write-host "Adding AITracker to the following sites:"
		foreach ($siteCollectionRootWebUrl in $config.TargetSites)
		{
			Write-Host " +" $siteCollectionRootWebUrl
		}
		Write-Host

		# Loop each site-collection in config
		foreach ($siteCollectionRootWebUrl in $config.TargetSites)
		{
            Write-Host "Processing site: $siteCollectionRootWebUrl..."
			InstallAITrackerToSiteCollection $siteCollectionRootWebUrl $config
		}

        Write-Host
        Write-Host (Get-Date).ToLongTimeString() "- AITracker.js uploaded to all site-collections and injected into all sub-sites!" -ForegroundColor Green -BackgroundColor Black

	}
	else
	{
		Write-host "Removing AITracker from the following sites:" -ForegroundColor Yellow
		foreach ($siteCollectionRootWebUrl in $config.TargetSites)
		{
			Write-Host " +" $siteCollectionRootWebUrl
		}
		Write-Host

		# Loop each site-collection in config
		foreach ($siteCollectionRootWebUrl in $config.TargetSites)
		{
            Write-Host "Processing site: $siteCollectionRootWebUrl..."
			UninstallAITrackerFromSiteCollection $siteCollectionRootWebUrl $config
		}

        Write-Host
        Write-Host (Get-Date).ToLongTimeString() "- AITracker.js uninstalled from all site-collections and sub-sites!" -ForegroundColor Green -BackgroundColor Black
	}

}


# Uninstall in a specific site-collection
function UninstallAITrackerFromSiteCollection($siteCollectionRootWebUrl, $config)
{
    # Set vars
    $sourceFile = $siteCollectionRootWebUrl + $sourceFileRelative

	# authenticate
	$Context = New-Object Microsoft.SharePoint.Client.ClientContext($siteCollectionRootWebUrl)
	$spoCredentials = New-Object Microsoft.SharePoint.Client.SharePointOnlineCredentials($username, $password)
	$Context.Credentials = $spoCredentials


	# Load SPWeb data & objects
	$web = $Context.Web
	try
	{
		$allWebs = $Context.Web.Webs
		$Context.Load($web)
        $Context.Load($web.Lists)
        $Context.Load($web.Lists)
        $Context.Load($allWebs)
		$Context.ExecuteQuery()

	}
	catch [System.Net.WebException]
	{
		Write-Host "ERROR: $username doesn't seem to have the right permissions to $siteCollectionRootWebUrl ?" -ForegroundColor Red
		return
	}

	# Upload JS to library in site-collection

    # Do we need to create SPOInsights DocLib?
    $list = $web.Lists | where{$_.Title -eq $config.SPOInsightDocLibTitle}
    if($list)
    {
        Write-Host "Destination list for AITracker '" $config.SPOInsightDocLibTitle "' exists already. Leaving it for now"
    }
    else
    {
        #Execute if list does not exists
        Write-Host "List " $config.SPOInsightDocLibTitle "does not exists. Not removing AITracker then" -ForegroundColor Yellow
		return
    }

    $List = $Context.Web.Lists.GetByTitle($config.SPOInsightDocLibTitle)


	# Check file doesn't exist already in site.
	Write-Host Checking if $sourceFile exists...
	try
	{
		$existingFile = $Context.Web.GetFileByUrl($sourceFile)
		$Context.Load($existingFile)
		$Context.ExecuteQuery()

		Write-host Removing AITracker.js... -ForegroundColor Yellow
		$existingFile.DeleteObject()

	}
	catch [System.Exception]
	{
		Write-Host "AITracker.js doesn't exist already - no need to uninstall."
	}


	# Add custom-action for this web. Sub-sites done recursively.
    Write-Host Removing custom-actions on all subsites that included AITracker.js...
    Write-Host
    RemoveAITrackerCustomActionFromWeb($web)
}

# Removes stuff from a site.
function RemoveAITrackerCustomActionFromWeb($web)
{
	$aiTrackerDecription = "SPO Insights AITracker"

	# Remove legacy AITracker custom-actions
    RemoveCustomActionByDescription $web "Core.EmbedJavaScriptJSOM" "ScriptLink"

	# Remove current AITracker custom-actions
    RemoveCustomActionByDescription $web $aiTrackerDecription "ScriptLink"

    # Remove modern UI extension too
    RemoveCustomActionByDescription $web $aiModernUITrackerDecription "ClientSideExtension.ApplicationCustomizer"

    # Check web sub-webs recursively
    #Write-Host ("Checking sub-webs for site '"+ $web.Title + "'...")
    foreach($subWeb in $web.Webs)
    {
        RemoveAITrackerCustomActionFromWeb($subWeb)
    }
}

# Refresh the file that contains the encrypted password. Run if authentication is failing
function RefreshSecureStringFile
{
    Write-Host "Refreshing secure-string file - please enter credentials" -ForegroundColor Green
    $creds = Get-Credential -Message ("Enter Admin Password") -UserName $username
    $fileContents = $creds.Password | ConvertFrom-SecureString

    $passwordTest = $fileContents | ConvertTo-SecureString
    $siteTestWorking = $true

	# Test login against all configured site-collections
	foreach ($siteCollectionRootWebUrl in $config.TargetSiteCollection)
	{
		try
		{
			$ContextTest = New-Object Microsoft.SharePoint.Client.ClientContext($siteCollectionRootWebUrl)
			$ContextTest.Credentials = New-Object Microsoft.SharePoint.Client.SharePointOnlineCredentials($username, $passwordTest)

			# Test Load SPWeb data & objects
			$web = $ContextTest.Web
			$allWebs = $ContextTest.Web.Webs
			$ContextTest.Load($web)
			$ContextTest.Load($allWebs)
			$ContextTest.ExecuteQuery()

		}
		catch
		{
            $siteTestWorking = $false
			Write-Host "ERROR: New credentials failed to authenticate: $Error[0].Message. Script will terminate." -ForegroundColor Magenta
		}
	}

    # Any error in any site?
    if ($siteTestWorking -eq $true)
    {

	    # If no exception, it must've worked...
	    if (!(Test-Path $SecureStringFile))
	    {
		    New-Item -path $SecureStringFile -type "file" -value $fileContents
		    Write-Host "Created $SecureStringFile with encrypted password value." -ForegroundColor Yellow
	    }
	    else
	    {
		    $fileContents | Out-File -FilePath $SecureStringFile
		    Write-Host "Updated $SecureStringFile with refreshed password"
	    }

	    Write-Host "CREDENTIALS REFRESHED: $SecureStringFile updated. Please run the script again to use saved credentials." -ForegroundColor Green
    }
	else
	{
		Write-Host "Please try that again - the credentials either are invalid or don't have access to the site-collections configured for installation." -ForegroundColor Yellow
	}
}

function RemoveCustomActionByDescription($web, $description, $location)
{
	# Load site data. Pretend we've just deleted a web-action so we enter the loop once at least.
    $loopActionCheck = $true
    $deletedAction = $false;

    # Loop all actions are deleted. Can't enumerate a collection that's deleted from
    while ($loopActionCheck)
    {
        $thisWebActions = $web.UserCustomActions
        $subWebs = $web.Webs
        $Context.Load($subWebs)
        $Context.Load($thisWebActions)
        $Context.ExecuteQuery()

        # find any previous custom actions of the same type & remove
        foreach ($action in $thisWebActions) {

            if (($action.Description -eq $description) -and ($action.Location -eq $location)) {

                $action.DeleteObject()
                $deletedAction = $true

                Write-Host "Removed custom-action with description '$description' from "$web.Url"." -ForegroundColor Yellow
			    $Context.ExecuteQuery();
                break
            }
        }

        # Did we delete an action?
        if ($deletedAction -eq $true)
        {
            # Go around again (and reset deleted flag).
            $loopActionCheck = $true
        }
        else
        {
            $loopActionCheck = $false;
        }
        $deletedAction = $false;
    }
}

# Add actions to SPWeb
function AddAITrackerCustomActionToWeb($web, $config)
{
	$aiTrackerDecription = "SPO Insights AITracker"

	# Remove legacy AITracker custom-actions
    RemoveCustomActionByDescription $web "Core.EmbedJavaScriptJSOM" "ScriptLink"

	# Remove current AITracker custom-actions
    RemoveCustomActionByDescription $web $aiTrackerDecription "ScriptLink"

    # Add new Action
	$thisWebActions = $web.UserCustomActions
    $newAction = $thisWebActions.Add()
    $newAction.Description = $aiTrackerDecription

    # Generate JS to inject into SharePoint pages
	$scriptBlock = @"
	var headID = document.getElementsByTagName("head")[0];var newScript = document.createElement("script");newScript.type = "text/javascript";newScript.src = "
"@

    $scriptBlock += $sourceFile + '?ver='+ (Get-Date).Ticks
    $scriptBlock += '";headID.appendChild(newScript);';

    # Insert action into host header + the AI key variable.
    $scriptBlock += 'var appInsightsConnectionStringHash = "' + $appInsightsConnectionStringEncoded + '";'
    $newAction.ScriptBlock = $scriptBlock
    $newAction.Location = "ScriptLink"

    $newAction.Update();
    try
    {
        $Context.ExecuteQuery()
        Write-Host ("Inserted custom-action into web: '" + ($web.Url) + "' with description '$aiTrackerDecription'...") -ForegroundColor Green
    }
    catch [Microsoft.SharePoint.Client.ServerUnauthorizedAccessException]
    {
        Write-Host "Failed to configure custom actions - custom scripts enabled? Run 'Set-SPOsite https://[tenant].sharepoint.com/sites/site -DenyAddAndCustomizePages 0' to enable customisations" -ForegroundColor Red

		# Sites must have customisations enabled.
        # Connect-SPOService -Url https://m365x818801-admin.sharepoint.com/
        # Set-SPOsite https://m365x818801.sharepoint.com/sites/SPOInsightsModern -DenyAddAndCustomizePages 0

    }

	# Add modern UI custom action to this web too.
	AddModernUIAITrackerCustomActionToWeb $web $config

    # Check web sub-webs recursively
    foreach($subWeb in $web.Webs)
    {
		Write-Host
		AddAITrackerCustomActionToWeb $subWeb $config
    }
}

# Add ModernUI solution to web
function AddModernUIAITrackerCustomActionToWeb($web, $config)
{

	# Remove current AITracker custom-actions
    RemoveCustomActionByDescription $web $aiModernUITrackerDecription "ClientSideExtension.ApplicationCustomizer"

	# NOTE - using direct CSOM here (rather than Add-PnPCustomAction) for now, due to https://github.com/SharePoint/PnP-PowerShell/issues/1048
	$customActions = $web.UserCustomActions
	$custAction = $customActions.Add()
	$custAction.Name = "AiTrackerModernApplicationCustomizer"
	$custAction.Title = "AiTrackerModernApplicationCustomizer"
	$custAction.Description = $aiModernUITrackerDecription

	$custAction.Location = "ClientSideExtension.ApplicationCustomizer"
	$custAction.ClientSideComponentId = "a4e24884-9cfd-41ac-87af-747a47055f25"

	# Include execution DT
	$dt = Get-Date -Format "dd-MM-yyyy:HH-mm-ss"
	$custAction.ClientSideComponentProperties = "{""appInsightsConnectionStringHash"":""" + $appInsightsConnectionStringEncoded + """, ""cacheToken"":""" + $dt + """}"

	write-host
	$custAction.Update()

	try
    {
        $Context.ExecuteQuery()
		Write-Host ("Inserted ModernUI Application Customizer custom-action into web: '" + ($web.Url) + "'...") -ForegroundColor Green
    }
    catch [Microsoft.SharePoint.Client.ServerUnauthorizedAccessException]
    {
        Write-Host "WARNING: Failed to configure custom actions - custom scripts enabled?" -ForegroundColor Yellow

		# Sites must have customisations enabled.
        # Connect-SPOService -Url https://m365x818801-admin.sharepoint.com/
        # Set-SPOsite https://m365x818801.sharepoint.com/sites/SPOInsightsModern -DenyAddAndCustomizePages 0

    }
}

# Install the things
ProcessScriptWithConfig($ConfigFileName)
