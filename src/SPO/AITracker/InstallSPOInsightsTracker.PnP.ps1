#
# InstallSPOInsightsTracker.ps1
#
# Installs AITracker.js into configured site-collections
# then adds custom-actions to all sub-sites with a reference to load it in the SharePoint sites.

Param 
(
	$ConfigFileName = "DevConfig.json", 	
	[switch] $UninstallOnly
)

function Get-ScriptDirectory {
	$Invocation = (Get-Variable MyInvocation -Scope 1).Value
	Split-Path $Invocation.MyCommand.Path
}

# Install custom action in all sites listed in the config
function ProcessScriptWithConfig ($configFileName) {

	# Load config and sanity check
	try {
		$config = Get-Content ($scriptPath + "\" + $configFileName) -Raw -ErrorAction Stop | ConvertFrom-Json
		Write-Host ("Read configuration for environment name '" + ($config.EnvironmentName) + "'...")
	}
	catch {
		Write-Host "FATAL ERROR: Cannot open config-file '$configFileName'" -ForegroundColor red -BackgroundColor Black
		return
	}

	# PnP Client ID. Check and encode to base64
	if ($config.PnPsClientId -eq $null -or $config.PnPsClientId -eq "") {
		Write-Host "FATAL ERROR: No property value with name 'PnPsClientId' in '$configFileName'. We need it to authenticate PnP PowerShell. Is this config file an old one?" -ForegroundColor red -BackgroundColor Black
		return
	}

	# App Insights Key. Check and encode to base64
	if ($config.ApplicationInsightsConnectionString -eq $null) {
		Write-Host "FATAL ERROR: No property value with name 'ApplicationInsightsConnectionString' in '$configFileName'. Is this config file an old one? We use Application Insights connection-strings instead of just instrumentation keys since Sep 2023" -ForegroundColor red -BackgroundColor Black
		return
	}
	$appInsightsConnectionStringBytes = [System.Text.Encoding]::UTF8.GetBytes($config.ApplicationInsightsConnectionString)
	$appInsightsConnectionStringEncoded = [Convert]::ToBase64String($appInsightsConnectionStringBytes)

	# Solution website base URL. Check and encode to base64
	if ($config.SolutionWebsiteBaseUrl -eq $null) {
		Write-Host "FATAL ERROR: No property value with name 'SolutionWebsiteBaseUrl' in '$configFileName' (eg 'https://o365analyticscontoso.azurewebsites.net/'). Is this config file an old one? This setting is required since July 2024" -ForegroundColor red -BackgroundColor Black
		return
	}
	$solutionWebsiteBaseUrlStringBytes = [System.Text.Encoding]::UTF8.GetBytes($config.SolutionWebsiteBaseUrl)
	$solutionWebsiteBaseUrlStringEncoded = [Convert]::ToBase64String($solutionWebsiteBaseUrlStringBytes)

	# Destination params
	$sourceFileRelative = $config.SourceFileRelativeDestination
	$targetFolder = $config.TargetFolderName

	# Output what we're going to do
	if ($UninstallOnly.IsPresent -ne $true) {
		$msg = "Adding AITracker to the following sites:"
	}
	else {
		$msg = "Removing AITracker from the following sites:"
	}

	Write-Output $msg

	foreach ($siteCollectionRootWebUrl in $config.TargetSites) {
		Write-Output " + $siteCollectionRootWebUrl"
	}

	Write-Output ""

	if ($UninstallOnly.IsPresent -ne $true) {
		# Loop each site-collection in config
		foreach ($siteCollectionRootWebUrl in $config.TargetSites) {
			Write-Host "Processing site: $siteCollectionRootWebUrl..."
			InstallAITrackerToSiteCollection $siteCollectionRootWebUrl $config
		}
		$msg = "Script finished"
	}
	else {		
		# Loop each site-collection in config
		foreach ($siteCollectionRootWebUrl in $config.TargetSites) {
			Write-Host "Processing site: $siteCollectionRootWebUrl..."
			UninstallAITrackerFromSiteCollection $siteCollectionRootWebUrl $config
		}
		
		$msg = (Get-Date).ToLongTimeString() + " - AITracker.js uninstalled from all site-collections and sub-sites!"	
		
	}

	Write-Host ""	
	Write-Host $msg -ForegroundColor Green -BackgroundColor Black
}

# Install in a specific site-collection
function InstallAITrackerToSiteCollection($siteCollectionRootWebUrl, $config) {
	# Set vars
	$sourceFile = $siteCollectionRootWebUrl + $sourceFileRelative


	$msg = "Successfuly connected to site: " + $siteCollectionRootWebUrl
	write-output $msg 
	
	Connect-PnPOnline -url $siteCollectionRootWebUrl -ClientId $config.PnPsClientId -Interactive -ErrorAction Stop
	$Context = Get-PnPContext
	$web = Get-PnPWeb -ErrorAction Stop

	Write-Output ""	
	
	# Create SPO Insights document library if it doesn't already exist
	$List = Confirm-SPODocumentLibrary -libName $config.SPOInsightDocLibTitle -libDescription "SPO Insight Asset Library"	

	if ($null -ne $List) {
		# Upload JS to library in site-collection
		# Read AITracker.js contents
		$aiTrackerFileName = ($scriptPath + "\AITracker.js")				

		# check if AITracker.js already exists
		$files = Find-PnPFile -List $config.SPOInsightDocLibTitle -Match "AITracker.js"
		
		if ($files.Count -gt 0) {
			Write-host "Removing previous AITracker.js instance(s)..." -ForegroundColor Yellow
			
			# normally there should not be more than 1 file 
			foreach ($f in $files) {
				# force check-in (in case file is checked out)
				Set-PnPFileCheckedIn -Url $f.ServerRelativeUrl
				Remove-PnPFile -ServerRelativeUrl $f.ServerRelativeUrl -Force
			}			
		}
		else {
			# Assuming we are running the script as a site collection admin, we are going to see all files in the library...
			#Write-Host "AITracker.js doesn't exist for user $username! Let's upload it..."
			
			Write-Host "AITracker.js doesn't exist! Let's upload it..."
		}
						
		if ($List.EnableMinorVersions) {
			$f = Add-PnPFile -Path $aiTrackerFileName -Folder $config.SPOInsightDocLibTitle -Publish
		}	
		else {
			$f = Add-PnPFile -Path $aiTrackerFileName -Folder $config.SPOInsightDocLibTitle
		}
		Write-Host "AITracker.js uploaded to site:"  $siteCollectionRootWebUrl "in document library: " $config.SPOInsightDocLibTitle "..." -ForegroundColor Green

		# Break security inheritance 
		Set-PnPList -Identity $config.SPOInsightDocLibTitle -BreakRoleInheritance -ClearSubscopes

		# Assign read-only permission to everyone
		Set-PnPListPermission -Identity $config.SPOInsightDocLibTitle -User 'c:0(.s|true' -AddRole 'Read'

		
		# Add custom-action for this web. Sub-sites done recursively.
		$msg = "Setting custom-action on all subsites to include AITracker.js in the HTML header..."
		Write-Host $msg
		Write-Host ""

		AddAITrackerCustomActionToWeb($web)
	}
	else {
		$msg = "Unexpected outcome. Unable to ensure existence of SPOInsightsAssets document library for site: '" + $siteCollectionRootWebUrl + "'"
		write-output $msg	
	}
}

# Uninstall in a specific site-collection
function UninstallAITrackerFromSiteCollection($siteCollectionRootWebUrl, $config) {	
	try {
		# Set vars
		$sourceFile = $siteCollectionRootWebUrl + $sourceFileRelative
	
	
		$Context = Get-PnPContext
		$web = Get-PnPWeb
	
		Write-Output ""	
	
		# Check whether SPO Insights document library exists
		$List = Confirm-SPODocumentLibrary -libName $config.SPOInsightDocLibTitle -readOnly
	
		if ($List -ne $null) {
			Write-Host "Destination list for AITracker '" $config.SPOInsightDocLibTitle "' exists already. Leaving it for now..."
			
			# Check if file exists on the site.
			Write-Host Checking if $sourceFile exists...		
		
			# check if AITracker.js already exists
			$files = Find-PnPFile -List $config.SPOInsightDocLibTitle -Match "AITracker.js"
	
			if ($files.Count -gt 0) {
				Write-host "Removing AITracker.js..." -ForegroundColor Yellow
				
				# normally there should not be more than 1 file 
				foreach ($f in $files) {
					# force check-in (in case file is checked out)
					Set-PnPFileCheckedIn -Url $f.ServerRelativeUrl
					Remove-PnPFile -ServerRelativeUrl $f.ServerRelativeUrl -Force
				}			
			}
			else {
				$msg = "AITracker.js does not exist on site '" + $siteCollectionRootWebUrl + "'. Nothing to uninstall."
				Write-Output 
			}			
	
			# Add custom-action for this web. Sub-sites done recursively.
			$msg = "Removing custom-actions on all subsites that included AITracker.js..."
			Write-Host $msg
			Write-Host ""
	
			RemoveAITrackerCustomActionFromWeb($web)
		}
		else {
			#Execute if list does not exist
			$msg = "List '" + $config.SPOInsightDocLibTitle + "' does not exist. Nothing to remove."
			Write-Host $msg -ForegroundColor Yellow
			return
		}
	}
	catch {
		$msg = "UninstallAITrackerFromSiteCollection: Unexpected error has occurred while connecting to site '" + $siteCollectionRootWebUrl + "'."
		Write-Output $msg

		$msg = "ERROR: " + $_.Exception.Message
		Write-Output $msg

		Write-Output ""
	}    
}

# check if a document library exists in a site, optionally create it if does not exist
function Confirm-SPODocumentLibrary ($libName, $libDescription, [switch] $readOnly) {
	$lib = $null		

	try {
		$lib = Get-PnPList -Identity $libName

		if (($lib.Title -eq $libName) -and ($lib.BaseTemplate -eq 101)) {
			$msg = "Document library by the name of '" + $libName + "' already exists in site '" + $lib.ParentWebUrl + "'. No need to re-create."
			write-output $msg
		}
		else {
			if (!$readOnly) {
				$msg = "Creating new document library: " + $libName
				write-output $msg

				$lib = New-PnPList -Title $libName -Template DocumentLibrary
				Set-PnPList -Identity $lib.Title -Description $libDescription
			}			
		}		
	}
	catch {
		$msg = "Error occurred while ensuring existence of document library: " + $libName
		Write-Output $msg
		$msg = "ERROR MESSAGE: " + $_.Exception.Message
		Write-Output $msg
	}
	
	return $lib
}


# Removes stuff from a site.
function RemoveAITrackerCustomActionFromWeb($web) {
	$aiTrackerDecription = "SPO Insights AITracker"

	# Remove legacy AITracker custom-actions
	RemoveCustomActionByDescription $web "Core.EmbedJavaScriptJSOM" "ScriptLink"

	# Remove current AITracker custom-actions
	RemoveCustomActionByDescription $web $aiTrackerDecription "ScriptLink"

	# Remove modern UI extension too
	RemoveCustomActionByDescription $web $aiModernUITrackerDecription "ClientSideExtension.ApplicationCustomizer"

	# Check web sub-webs recursively
	#Write-Host ("Checking sub-webs for site '"+ $web.Title + "'...")
	foreach ($subWeb in $web.Webs) {
		RemoveAITrackerCustomActionFromWeb($subWeb)
	}
}


function RemoveCustomActionByDescription($web, $description, $location) {
	# Load site data. Pretend we've just deleted a web-action so we enter the loop once at least.
	$loopActionCheck = $true
	$deletedAction = $false;
	
	#Get-PnPCustomAction -Scope All | ? Location -eq ScriptLink -and Description -eq $description | Remove-PnPCustomAction
	#	$actions = Get-PnPCustomAction -Scope All | ? (Location -eq $location) -and (Description -eq $description)

	# Loop all actions are deleted. Can't enumerate a collection that's deleted from
	while ($loopActionCheck) {
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
		if ($deletedAction -eq $true) {
			# Go around again (and reset deleted flag).
			$loopActionCheck = $true
		}
		else {
			$loopActionCheck = $false;
		}
		$deletedAction = $false;
	}
}

# Add actions to SPWeb
function AddAITrackerCustomActionToWeb($web) {
	$aiTrackerDecription = "SPO Insights AITracker"

	# Remove legacy AITracker custom-actions
	RemoveCustomActionByDescription $web "Core.EmbedJavaScriptJSOM" "ScriptLink"

	# Remove current AITracker custom-actions
	RemoveCustomActionByDescription $web $aiTrackerDecription "ScriptLink"

	# Generate JS to inject into SharePoint pages
	$scriptBlock = @"
	var headID = document.getElementsByTagName("head")[0];var newScript = document.createElement("script");newScript.type = "text/javascript";newScript.src = "
"@

	$scriptBlock += $sourceFile + '?ver=' + (Get-Date).Ticks
	$scriptBlock += '";headID.appendChild(newScript);';

	# Insert action into host header + the AI key variable.
	$scriptBlock += 'var appInsightsConnectionStringHash = "' + $appInsightsConnectionStringEncoded + '";'

	# Insert root 
	$scriptBlock += 'var insightsWebRootUrlHash = "' + $solutionWebsiteBaseUrlStringEncoded + '";'
	
	#Add-PnPCustomAction -Description $description -Location "ScriptLink" -scr $scriptBlock

	# Add new Action
	$thisWebActions = $web.UserCustomActions
	$newAction = $thisWebActions.Add()
	$newAction.Description = $aiTrackerDecription
	$newAction.ScriptBlock = $scriptBlock
	$newAction.Location = "ScriptLink"
	$newAction.Update();
	
	try {
		$Context.ExecuteQuery()
		Write-Host ("Inserted custom-action into web: '" + ($web.Url) + "' with description '$aiTrackerDecription'...") -ForegroundColor Green
	}
	catch {
		Write-Host "WARNING: Failed to configure custom actions - custom scripts enabled? Run 'Set-SPOsite $web.Url -DenyAddAndCustomizePages 0' to enable customisations" -ForegroundColor Yellow

		# Sites must have customisations enabled.
		# Connect-SPOService -Url https://m365x818801-admin.sharepoint.com/
		# Set-SPOsite https://m365x818801.sharepoint.com/sites/SPOInsightsModern -DenyAddAndCustomizePages 0

	}

	# Add modern UI custom action to this web too.
	AddModernUIAITrackerCustomActionToWeb($web)

	# Check web sub-webs recursively
	foreach ($subWeb in $web.Webs) {
		Write-Host
		AddAITrackerCustomActionToWeb($subWeb)
	}
}

# Add ModernUI solution to web
function AddModernUIAITrackerCustomActionToWeb($web) {

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

	# Build the properties string
	$custAction.ClientSideComponentProperties = "{""appInsightsConnectionStringHash"":""" + $appInsightsConnectionStringEncoded + """, " + 
	"""insightsWebRootUrlHash"":""" + $solutionWebsiteBaseUrlStringEncoded + """, ""cacheToken"":""" + $dt + """}"

	$custAction.Update()

	try {
		$Context.ExecuteQuery()
		Write-Host ("Inserted ModernUI Application Customizer custom-action into web: '" + ($web.Url) + "'...") -ForegroundColor Green
	}
	catch [Microsoft.SharePoint.Client.ServerUnauthorizedAccessException] {
		Write-Host "Failed to configure custom actions - custom scripts enabled?" -ForegroundColor Red

		# Sites must have customisations enabled.
		# Connect-SPOService -Url https://m365x818801-admin.sharepoint.com/
		# Set-SPOsite https://m365x818801.sharepoint.com/sites/SPOInsightsModern -DenyAddAndCustomizePages 0
	}
}


$scriptPath = Get-ScriptDirectory

# Install the things
if ($PSVersionTable.PSVersion.Major -lt 7) {
	Write-Host "Unsupported PowerShell version detected. This script only supports PowerShell 7 - https://pnp.github.io/powershell/articles/installation.html" -ForegroundColor red
}
else {
	
	# Install PnP module?
	if (Get-Module -ListAvailable -Name PnP.PowerShell) {
		Write-Host "SharePoint PnP PowerShell is installed."
	} 
	else {
		Write-Host "SharePoint PnP PowerShell not installed. Installing now for current user..."
		Install-Module PnP.PowerShell -Scope CurrentUser -SkipPublisherCheck
	}

	Import-Module PnP.PowerShell

	
	$aiModernUITrackerDecription = "SPO Insights ModernUI AITracker App Customizer"
	
	ProcessScriptWithConfig($ConfigFileName)
}
