#Requires -Version 5.1
# Run with: Invoke-Pester .\Deploy-AppServiceContent.Tests.ps1 -EnableExit
. "$PSScriptRoot\Deploy-AppServiceContent.ps1" -WebAppName contoso-analytics

Describe 'Website deployment lifecycle' {
    BeforeEach {
        $script:calls = New-Object 'System.Collections.Generic.List[string]'
        $script:state = 'Running'
        $script:uploadFails = $false
        $script:stopFails = $false
        $script:startFails = $false
        $script:auth = [pscustomobject]@{
            ScmHost = 'contoso-analytics.scm.azurewebsites.net'
            Headers = @{}
            Kind = 'Basic'
        }
        Mock Resolve-AppServiceControl { return [pscustomobject]@{ Uri = 'https://management.azure.com/contoso'; Headers = @{} } }
        Mock Get-AppServiceState { return $script:state }
        Mock Set-AppServiceState {
            param($Control, $Action)
            $script:calls.Add($Action)
            if ($Action -eq 'stop' -and $script:stopFails) { throw 'stop failed' }
            if ($Action -eq 'start' -and $script:startFails) { throw 'start failed' }
        }
        Mock Invoke-KuduZipDeploy {
            $script:calls.Add('upload')
            if ($script:uploadFails) { throw 'upload failed' }
        }
    }

    It 'stops before upload and restarts after success' {
        Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip'
        ($script:calls -join ',') | Should Be 'stop,upload,start'
        Assert-MockCalled Invoke-KuduZipDeploy -Times 1 -Exactly -Scope It -ParameterFilter {
            $RemotePath -eq '/site/wwwroot/' -and $RetryFileLocks
        }
    }

    It 'preserves an already stopped app' {
        $script:state = 'Stopped'
        Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip'
        ($script:calls -join ',') | Should Be 'upload'
    }

    It 'restores running state and propagates upload failure' {
        $script:uploadFails = $true
        { Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip' } | Should Throw 'upload failed'
        ($script:calls -join ',') | Should Be 'stop,upload,start'
    }

    It 'does not start an already stopped app after upload failure' {
        $script:state = 'Stopped'
        $script:uploadFails = $true
        { Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip' } | Should Throw 'upload failed'
        ($script:calls -join ',') | Should Be 'upload'
    }

    It 'preserves the upload response body through restoration' {
        Mock Invoke-KuduZipDeploy {
            $errorRecord = New-Object System.Management.Automation.ErrorRecord(
                (New-Object System.Exception('upload failed')), 'UploadFailed',
                [System.Management.Automation.ErrorCategory]::InvalidOperation, $null)
            $errorRecord.ErrorDetails = New-Object System.Management.Automation.ErrorDetails('synthetic response')
            throw $errorRecord
        }
        try {
            Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip'
            throw 'Expected failure'
        } catch {
            $_.ErrorDetails.Message | Should Be 'synthetic response'
        }
        ($script:calls -join ',') | Should Be 'stop,start'
    }

    It 'does not upload if shutdown fails and still attempts restoration' {
        $script:stopFails = $true
        { Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip' } | Should Throw 'stop failed'
        ($script:calls -join ',') | Should Be 'stop,start'
    }

    It 'propagates restart failure after a successful upload' {
        $script:startFails = $true
        { Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip' } | Should Throw 'start failed'
    }

    It 'reports restoration failure without hiding the original upload failure' {
        $script:uploadFails = $true
        $script:startFails = $true
        Mock Write-ErrMsg {}
        { Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip' } | Should Throw 'upload failed'
        Assert-MockCalled Write-ErrMsg -Times 1 -Exactly -Scope It -ParameterFilter { $m -like '*start failed*' }
    }

    It 'rejects unexpected initial state without uploading or starting' {
        $script:state = 'Unknown'
        { Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip' } | Should Throw 'Cannot safely deploy'
        $script:calls.Count | Should Be 0
    }

    It 'does not mutate the app if management preflight fails' {
        Mock Resolve-AppServiceControl { throw 'management access denied' }
        { Invoke-WebsiteDeployment -Auth $script:auth -ZipPath 'Website.zip' } | Should Throw 'management access denied'
        $script:calls.Count | Should Be 0
    }
}

Describe 'App Service state transitions' {
    BeforeEach {
        $script:reads = 0
        $script:control = [pscustomobject]@{ Uri = 'https://management.azure.com/contoso'; Headers = @{} }
        Mock Invoke-RestMethod {}
        Mock Start-Sleep {}
    }

    It 'waits for stopped state before returning' {
        Mock Get-AppServiceState {
            $script:reads++
            if ($script:reads -lt 3) { return 'Running' }
            return 'Stopped'
        }
        Set-AppServiceState -Control $script:control -Action stop
        $script:reads | Should Be 3
        Assert-MockCalled Start-Sleep -Times 2 -Exactly -Scope It
        Assert-MockCalled Invoke-RestMethod -Times 1 -Exactly -Scope It -ParameterFilter {
            $Method -eq 'Post' -and $Uri -eq 'https://management.azure.com/contoso/stop?api-version=2024-11-01'
        }
    }

    It 'waits for running state after restart' {
        Mock Get-AppServiceState { return 'Running' }
        Set-AppServiceState -Control $script:control -Action start
        Assert-MockCalled Invoke-RestMethod -Times 1 -Exactly -Scope It -ParameterFilter {
            $Method -eq 'Post' -and $Uri -like '*/start?api-version=*'
        }
    }
}

Describe 'Website management preflight' {
    BeforeEach {
        $ResourceGroup = 'rg-contoso'
        $SubscriptionId = '00000000-0000-0000-0000-000000000000'
        $AccessToken = 'synthetic-token'
        $script:auth = [pscustomobject]@{
            ScmHost = 'contoso-analytics.scm.azurewebsites.net'
            Headers = @{}
            Kind = 'Basic'
        }
        Mock Invoke-RestMethod {
            return [pscustomobject]@{ properties = [pscustomobject]@{
                enabledHostNames = @('contoso-analytics.azurewebsites.net', 'contoso-analytics.scm.azurewebsites.net')
                state = 'Running'
            } }
        }
    }

    It 'uses the explicit subscription and a management token with Basic SCM auth' {
        $control = Resolve-AppServiceControl -Auth $script:auth
        $control.Uri | Should Be 'https://management.azure.com/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-contoso/providers/Microsoft.Web/sites/contoso-analytics'
        $control.Headers.Authorization | Should Be 'Bearer synthetic-token'
    }

    It 'requires the resource group before making management requests' {
        $ResourceGroup = ''
        { Resolve-AppServiceControl -Auth $script:auth } | Should Throw 'requires -ResourceGroup'
        Assert-MockCalled Invoke-RestMethod -Times 0 -Exactly -Scope It
    }

    It 'rejects mismatched SCM targets before any stop request' {
        $script:auth.ScmHost = 'contoso-other.scm.azurewebsites.net'
        { Resolve-AppServiceControl -Auth $script:auth } | Should Throw 'does not match'
        Assert-MockCalled Invoke-RestMethod -Times 0 -Exactly -Scope It -ParameterFilter { $Method -eq 'Post' }
    }

    It 'fails clearly when publishing credentials have no management token' {
        $AccessToken = ''
        Mock Get-ArmAccessTokenAuto { return $null }
        { Resolve-AppServiceControl -Auth $script:auth } | Should Throw 'requires an Azure management token'
        Assert-MockCalled Invoke-RestMethod -Times 0 -Exactly -Scope It
    }
}

Describe 'Kudu file-lock retries' {
    BeforeEach {
        $script:attempts = 0
        $script:body = '{"ExceptionType":"System.IO.IOException","ExceptionMessage":"The process cannot access the file because it is being used by another process."}'
        Mock Get-HttpStatus { return 400 }
        Mock Get-HttpErrorBody { return $script:body }
        Mock Start-Sleep {}
    }

    It 'retries the specific Kudu file-in-use 400 when requested' {
        Invoke-WithRetry -RetryFileLocks -MaxAttempts 3 -Script {
            $script:attempts++
            if ($script:attempts -lt 3) { throw 'locked' }
        }
        $script:attempts | Should Be 3
        Assert-MockCalled Start-Sleep -Times 2 -Exactly -Scope It
    }

    It 'does not retry that 400 outside website deployment' {
        { Invoke-WithRetry -MaxAttempts 3 -Script { $script:attempts++; throw 'locked' } } | Should Throw
        $script:attempts | Should Be 1
    }

    It 'does not retry an unrelated IOException' {
        $script:body = '{"ExceptionType":"System.IO.IOException","ExceptionMessage":"Disk full"}'
        { Invoke-WithRetry -RetryFileLocks -MaxAttempts 3 -Script { $script:attempts++; throw 'disk full' } } | Should Throw
        $script:attempts | Should Be 1
    }

    It 'does not retry a different exception even with matching message text' {
        $script:body = '{"ExceptionType":"System.InvalidOperationException","ExceptionMessage":"because it is being used by another process"}'
        { Invoke-WithRetry -RetryFileLocks -MaxAttempts 3 -Script { $script:attempts++; throw 'bad request' } } | Should Throw
        $script:attempts | Should Be 1
    }

    It 'still retries ordinary transient HTTP failures' {
        Mock Get-HttpStatus { return 503 }
        Invoke-WithRetry -MaxAttempts 2 -Script {
            $script:attempts++
            if ($script:attempts -eq 1) { throw 'unavailable' }
        }
        $script:attempts | Should Be 2
    }

    It 'does not retry invalid error JSON' {
        $script:body = 'Bad Request'
        { Invoke-WithRetry -RetryFileLocks -MaxAttempts 3 -Script { $script:attempts++; throw 'bad request' } } | Should Throw
        $script:attempts | Should Be 1
    }

    It 'stops retrying and preserves the body when the lock persists' {
        try {
            Invoke-WithRetry -RetryFileLocks -MaxAttempts 2 -Script { $script:attempts++; throw 'locked' }
            throw 'Expected failure'
        } catch {
            $_.Exception.Message | Should Be 'locked'
            $_.ErrorDetails.Message | Should Be $script:body
        }
        $script:attempts | Should Be 2
    }
}
