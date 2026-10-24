function Test-StoreSubmissionBusy {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$StatusOutput)

    if ($StatusOutput -notmatch 'Submission Status = (\S+)') { return $false }
    $busyStates = 'CommitStarted', 'PreProcessing', 'PendingPublication', 'Publishing', 'Release'
    return $busyStates -contains $Matches[1]
}

function Get-StoreApiOnlySubmissionId {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Output)

    if ($Output -notmatch 'can only update, delete, and commit submissions that are created through the API') { return $null }
    if ($Output -match "Found (?:Flight )?Submission with Id '([^']+)'") { return $Matches[1] }
    return 'unknown'
}

function Wait-StoreSubmissionClear {
    param(
        [Parameter(Mandatory)][string]$ProductId,
        [string]$FlightId,
        [int]$TimeoutMinutes = 60,
        [int]$PollSeconds = 120
    )

    $statusArgs = if ($FlightId) { @('flights', 'submission', 'status', $ProductId, $FlightId) } else { @('submission', 'status', $ProductId) }
    $deleteArgs = if ($FlightId) { @('flights', 'submission', 'delete', $ProductId, $FlightId, '--no-confirm') } else { @('submission', 'delete', $ProductId, '--no-confirm') }

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ($true) {
        $statusOut = & msstore @statusArgs 2>&1 | Out-String
        Write-Host $statusOut

        $apiOnlyId = Get-StoreApiOnlySubmissionId -Output $statusOut
        if ($apiOnlyId) {
            Write-Host "::error::Submission $apiOnlyId was created in Partner Center; delete it by hand there (Earmark 9N27KG5M9W9B) before this job can run again."
            exit 1
        }

        if (Test-StoreSubmissionBusy -StatusOutput $statusOut) {
            if ((Get-Date) -ge $deadline) {
                Write-Host "::error::Submission is still processing after $TimeoutMinutes minutes; giving up."
                exit 1
            }
            Write-Host "Submission still processing; waiting $PollSeconds seconds before checking again."
            Start-Sleep -Seconds $PollSeconds
            continue
        }

        # msstore writes its progress and errors to stderr; seen 14 Oct 2026, "cannot be deleted" was missed without this.
        $deleteOut = & msstore @deleteArgs 2>&1 | Out-String
        Write-Host $deleteOut
        $global:LASTEXITCODE = 0

        $apiOnlyId = Get-StoreApiOnlySubmissionId -Output $deleteOut
        if ($apiOnlyId) {
            Write-Host "::error::Submission $apiOnlyId was created in Partner Center; delete it by hand there (Earmark 9N27KG5M9W9B) before this job can run again."
            exit 1
        }

        if ($deleteOut -match 'cannot be deleted') {
            if ((Get-Date) -ge $deadline) {
                Write-Host "::error::Pending submission still refuses deletion after $TimeoutMinutes minutes; giving up."
                exit 1
            }
            Write-Host "Pending submission cannot be deleted yet; waiting $PollSeconds seconds before checking again."
            Start-Sleep -Seconds $PollSeconds
            continue
        }

        return
    }
}

function Test-StoreSubmissionHasPackage {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Output,
        [Parameter(Mandatory)][string]$PackageName
    )

    return $Output -match ('"FileName":\s*"' + [regex]::Escape($PackageName) + '"')
}

function Get-StorePendingSubmission {
    param(
        [Parameter(Mandatory)][string]$ProductId,
        [Parameter(Mandatory)][string]$PackagePath,
        [string]$FlightId,
        [int]$Attempts = 10,
        [int]$PollSeconds = 30
    )

    $getArgs = if ($FlightId) { @('flights', 'submission', 'get', $ProductId, $FlightId) } else { @('submission', 'get', $ProductId) }
    $packageName = Split-Path $PackagePath -Leaf

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $out = & msstore @getArgs | Out-String
        if ($LASTEXITCODE -ne 0) { throw "msstore $($getArgs -join ' ') failed ($LASTEXITCODE)" }
        # Seen 4 Oct 2026 (0.20.2): right after publish --noCommit, get answered with the last published submission, and patching that shipped the previous package.
        if (Test-StoreSubmissionHasPackage -Output $out -PackageName $packageName) {
            $s = $out.IndexOf('{'); $e = $out.LastIndexOf('}')
            if ($s -lt 0 -or $e -le $s) { throw 'Could not parse submission JSON from msstore output' }
            return $out.Substring($s, $e - $s + 1) | ConvertFrom-Json
        }
        Write-Host "Pending submission does not list $packageName yet; waiting $PollSeconds seconds (attempt $attempt of $Attempts)."
        Start-Sleep -Seconds $PollSeconds
    }

    Write-Host "::error::No pending submission lists $packageName after $Attempts attempts; not committing."
    exit 1
}

function Hide-StoreUploadUrl {
    param([Parameter(Mandatory)]$Submission)

    # FileUploadUrl is a write-enabled SAS URL for the package blob; this repo's logs are public.
    $url = $Submission.FileUploadUrl
    if (-not $url) { return }
    Write-Host "::add-mask::$url"
    Write-Host "::add-mask::$($url.Replace('&', '\' + 'u0026'))"
    if ($url -match '[?&]sig=([^&]+)') { Write-Host "::add-mask::$($Matches[1])" }
}

if ($MyInvocation.InvocationName -ne '.') {
    if (Test-StoreSubmissionBusy -StatusOutput 'Submission Status = Certification') { throw 'self-check failed: Certification should be deletable' }
    if (Test-StoreSubmissionBusy -StatusOutput 'Submission Status = PendingCommit') { throw 'self-check failed: PendingCommit should be deletable' }
    if (-not (Test-StoreSubmissionBusy -StatusOutput 'Submission Status = CommitStarted')) { throw 'self-check failed: CommitStarted should be busy' }
    if (Test-StoreSubmissionBusy -StatusOutput 'Submission Status = Published') { throw 'self-check failed: Published should not be busy' }
    if (Test-StoreSubmissionBusy -StatusOutput 'Submission Status = CommitFailed') { throw 'self-check failed: CommitFailed should not be busy' }
    if (Test-StoreSubmissionBusy -StatusOutput 'no status line here') { throw 'self-check failed: missing status line should not be busy' }

    $flightError = "Found Flight Submission with Id 'abc-123'`nIngestion API can only update, delete, and commit submissions that are created through the API."
    if ((Get-StoreApiOnlySubmissionId -Output $flightError) -ne 'abc-123') { throw 'self-check failed: expected to extract the flight submission id' }
    if (Get-StoreApiOnlySubmissionId -Output 'Existing submission deleted!') { throw 'self-check failed: normal delete output should not match' }
    if (Get-StoreApiOnlySubmissionId -Output '') { throw 'self-check failed: an empty delete output (nothing pending) should not match' }
    if (Test-StoreSubmissionBusy -StatusOutput '') { throw 'self-check failed: an empty status output should not be busy' }

    $published = "Could not find a Pending Submission, but found the Last Published Submission.`n      `"FileName`": `"Earmark-1.1.378.0-Store.msixupload`","
    if (Test-StoreSubmissionHasPackage -Output $published -PackageName 'Earmark-1.1.387.0-Store.msixupload') { throw 'self-check failed: the published submission should not list the new package' }
    $pending = "      `"FileName`": `"Earmark-1.1.378.0-Store.msixupload`",`n      `"FileName`": `"Earmark-1.1.387.0-Store.msixupload`","
    if (-not (Test-StoreSubmissionHasPackage -Output $pending -PackageName 'Earmark-1.1.387.0-Store.msixupload')) { throw 'self-check failed: the pending submission should list the new package' }

    Write-Host 'Wait-StoreSubmissionClear self-check passed.'
}
