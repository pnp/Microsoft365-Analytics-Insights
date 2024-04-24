#
# InstallSPOInsightsTracker-OnPrem.ps1
#
# Installs AITracker.js into configured site-collections for SharePoint Server.
# SharePoint Online installations should use InstallSPOInsightsTracker.ps1


Param(
    $ConfigFileName = "DevConfig.json"
)

# SharePoint 2016 CSOM. Change as appropriate for other versions of SharePoint.
Add-Type -Path "C:\Program Files\Common Files\Microsoft Shared\Web Server Extensions\16\ISAPI\Microsoft.SharePoint.Client.dll"
Add-Type -Path "C:\Program Files\Common Files\Microsoft Shared\Web Server Extensions\16\ISAPI\Microsoft.SharePoint.Client.Runtime.dll"



function Get-ScriptDirectory
{
    $Invocation = (Get-Variable MyInvocation -Scope 1).Value
    Split-Path $Invocation.MyCommand.Path
}
$scriptPath = Get-ScriptDirectory
$spoCredentials = $null
# InstallAITrackerWithConfig($ConfigFileName) called at end of script

# Install in a specific site-collection
function InstallAITrackerToSiteCollection($siteCollectionRootWebUrl, $config)
{
    # Set vars
    $sourceFile = $siteCollectionRootWebUrl + $sourceFileRelative

	# authenticate
	$Context = New-Object Microsoft.SharePoint.Client.ClientContext($siteCollectionRootWebUrl)
	$spoCredentials = New-Object System.Net.NetworkCredential($username, $password)
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

	# Read AITracker.js contents
	$aiTrackerFileName = ($scriptPath + "\AITracker.js")
	$FileStream = New-Object IO.FileStream($aiTrackerFileName,[System.IO.FileMode]::Open) -ErrorAction:Stop

	# Exit if source file couldn't be read
	if ($FileStream -eq $null) {
		Write-Host "Local AITracker.js couldn't be read!" -ForegroundColor Red
		return
	}

	# Check file doesn't exist already in site.
	Write-Host Checking if $sourceFile exists...
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
		Write-Host "AITracker.js doesn't exist for user $username!" -ForegroundColor Green
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
    AddAITrackerCustomActionToWeb($web)
}

# Install in all sites listed in the config
function InstallAITrackerWithConfig($configFileName)
{
    # Load config
    try
	{
        $config = Get-Content ($scriptPath + "\" + $configFileName) -Raw -ErrorAction Stop | ConvertFrom-Json
        Write-Host ("Read configuration for environment name '" + ($config.EnvironmentName) + "'...")
	}
	catch
	{
		Write-Host "FATAL ERROR: Cannot open config-file '$configFileName': $_.Exception.Message" -ForegroundColor red -BackgroundColor Black
		return
	}

	# Set Params
	# Credentials need site-collection admin rights
	$username = $config.AdminUsername
	$password = ""

	# App Insights Key
	$appInsightsKey = $config.ApplicationInsightsKey

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
    Write-host "Adding AITracker to the following sites:"
    foreach ($siteCollectionRootWebUrl in $config.TargetSites)
    {
        Write-Host " +" $siteCollectionRootWebUrl
    }
    Write-Host

	# Loop each site-collection in config
    foreach ($siteCollectionRootWebUrl in $config.TargetSites)
    {
		InstallAITrackerToSiteCollection $siteCollectionRootWebUrl $config
	}

	Write-Host
    Write-Host (Get-Date).ToLongTimeString() "- AITracker.js uploaded to all site-collections and injected into all sub-sites!" -ForegroundColor Green -BackgroundColor Black
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
        Write-Host Checking SPWeb.UserCustomActions...
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
function AddAITrackerCustomActionToWeb($web)
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
    $scriptBlock += 'var spoInsightsIntrumentationKey = "' + $appInsightsKey + '";'
    $newAction.ScriptBlock = $scriptBlock
    $newAction.Location = "ScriptLink"

    $newAction.Update();
    try
    {
        $Context.ExecuteQuery()
        Write-Host ("Inserted custom-action into web: '" + ($web.Url) + "'...") -ForegroundColor Green
    }
    catch [Microsoft.SharePoint.Client.ServerUnauthorizedAccessException]
    {
        Write-Host "Failed to configure custom actions - custom scripts enabled? Run 'Set-SPOsite https://[tenant].sharepoint.com/sites/site -DenyAddAndCustomizePages 0' to enable customisations" -ForegroundColor Red

		# Sites must have customisations enabled.
        # Connect-SPOService -Url https://m365x818801-admin.sharepoint.com/
        # Set-SPOsite https://m365x818801.sharepoint.com/sites/SPOInsightsModern -DenyAddAndCustomizePages 0

    }

	# Add modern UI custom action to this web too.
	# AddModernUIAITrackerCustomActionToWeb($subWeb) # Not available yet in SharePoint Server (SPO only)


    # Check web sub-webs recursively
    Write-Host ("Checking sub-webs for site '"+ $web.Title + "'...")
    foreach($subWeb in $web.Webs)
    {
        AddAITrackerCustomActionToWeb($subWeb)
    }
}


# Install the things
InstallAITrackerWithConfig($ConfigFileName)
