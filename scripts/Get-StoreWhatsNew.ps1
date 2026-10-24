function Get-StoreWhatsNew {
    param(
        [Parameter(Mandatory)][string]$ChangelogPath,
        [Parameter(Mandatory)][string]$Version,
        [string]$SinceVersion,
        [int]$Limit = 1500
    )

    $versionParsed = [version]$Version
    $sinceParsed = if ($SinceVersion) { [version]$SinceVersion } else { $null }

    $sections = [ordered]@{}
    $currentVersion = $null
    $currentSection = $null
    foreach ($line in Get-Content -Encoding utf8 $ChangelogPath) {
        if ($line -match '^## \[?(\d+\.\d+\.\d+)') {
            $currentVersion = $Matches[1]
            $currentSection = $null
            if (-not $sections.Contains($currentVersion)) {
                $sections[$currentVersion] = @{
                    Features    = [System.Collections.Generic.List[string]]::new()
                    'Bug Fixes' = [System.Collections.Generic.List[string]]::new()
                }
            }
            continue
        }
        if (-not $currentVersion) { continue }
        if ($line -match '^### (.+)') { $currentSection = $Matches[1].Trim(); continue }
        if ($currentSection -notin @('Features', 'Bug Fixes')) { continue }
        if ($line -notmatch '^\* ') { continue }

        $text = $line.Substring(2) -replace '^\*\*[^*]+:\*\*\s*', ''
        $text = ($text -replace '\s*\(\[.*$', '').Trim()
        if ($text) { $sections[$currentVersion][$currentSection].Add($text) }
    }

    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($v in $sections.Keys) {
        $parsed = [version]$v
        if ($parsed -gt $versionParsed) { continue }
        $inRange = if ($SinceVersion) { $parsed -gt $sinceParsed } else { $parsed -eq $versionParsed }
        if (-not $inRange) { continue }
        $items.AddRange($sections[$v]['Features'])
        $items.AddRange($sections[$v]['Bug Fixes'])
    }

    if ($items.Count -eq 0) { return 'Minor improvements and fixes.' }

    $kept = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $items) { $kept.Add("- $item") }
    $notes = $kept -join "`n"
    while ($notes.Length -gt $Limit) {
        $kept.RemoveAt($kept.Count - 1)
        $dropped = $items.Count - $kept.Count
        $notes = (@($kept) + "- and $dropped more $(if ($dropped -eq 1) { 'change' } else { 'changes' })") -join "`n"
    }
    return $notes
}

if ($MyInvocation.InvocationName -ne '.') {
    $result = Get-StoreWhatsNew -ChangelogPath (Join-Path $PSScriptRoot '..\CHANGELOG.md') -Version '1.1.0'
    if (-not $result) { throw 'self-check failed: expected notes for 1.1.0' }
    Write-Host $result
    Write-Host "Length: $($result.Length)"

    $scopedChangelog = New-TemporaryFile
    Set-Content -Path $scopedChangelog -Encoding utf8 -Value @(
        '## [1.2.3](https://example.com) (2026-09-26)'
        ''
        '### Bug Fixes'
        ''
        '* **audio:** stop the retry loop from spinning forever ([abc1234](https://example.com))'
    )
    try {
        $scopedResult = Get-StoreWhatsNew -ChangelogPath $scopedChangelog -Version '1.2.3'
        if ($scopedResult -ne '- stop the retry loop from spinning forever') { throw "self-check failed: scoped entry gave '$scopedResult'" }
    }
    finally {
        Remove-Item -LiteralPath $scopedChangelog -ErrorAction SilentlyContinue
    }

    $longChangelog = New-TemporaryFile
    Set-Content -Path $longChangelog -Encoding utf8 -Value @(
        '## [1.2.5](https://example.com) (2026-09-26)'
        ''
        '### Features'
        ''
        '* first change ([abc1234](https://example.com))'
        '* second change ([abc1234](https://example.com))'
        '* third change ([abc1234](https://example.com))'
    )
    try {
        $truncatedResult = Get-StoreWhatsNew -ChangelogPath $longChangelog -Version '1.2.5' -Limit 40
        if ($truncatedResult -ne "- first change`n- and 2 more changes") { throw "self-check failed: truncation gave '$truncatedResult'" }
    }
    finally {
        Remove-Item -LiteralPath $longChangelog -ErrorAction SilentlyContinue
    }

    $emptyChangelog = New-TemporaryFile
    Set-Content -Path $emptyChangelog -Encoding utf8 -Value @(
        '## [1.2.4](https://example.com) (2026-09-26)'
        ''
        '### Miscellaneous Chores'
        ''
        '* bump a dependency ([abc1234](https://example.com))'
    )
    try {
        $noNotesResult = Get-StoreWhatsNew -ChangelogPath $emptyChangelog -Version '1.2.4'
        if ($noNotesResult -ne 'Minor improvements and fixes.') { throw "self-check failed: expected the fallback notes, got '$noNotesResult'" }
    }
    finally {
        Remove-Item -LiteralPath $emptyChangelog -ErrorAction SilentlyContinue
    }

    $multiSectionChangelog = New-TemporaryFile
    Set-Content -Path $multiSectionChangelog -Encoding utf8 -Value @(
        '## [2.1.0](https://example.com) (2026-09-27)'
        ''
        '### Bug Fixes'
        ''
        '* fix from the new release ([abc1234](https://example.com))'
        ''
        '## [2.0.1](https://example.com) (2026-09-26)'
        ''
        '### Features'
        ''
        '* feature from the skipped draft ([abc1234](https://example.com))'
        ''
        '## [2.0.0](https://example.com) (2026-09-20)'
        ''
        '### Features'
        ''
        '* headline feature ([abc1234](https://example.com))'
        ''
        '## [1.9.0](https://example.com) (2026-09-01)'
        ''
        '### Features'
        ''
        '* older feature out of range ([abc1234](https://example.com))'
    )
    try {
        $sinceResult = Get-StoreWhatsNew -ChangelogPath $multiSectionChangelog -Version '2.1.0' -SinceVersion '1.9.0'
        $expected = "- fix from the new release`n- feature from the skipped draft`n- headline feature"
        if ($sinceResult -ne $expected) { throw "self-check failed: SinceVersion combine gave '$sinceResult'" }
    }
    finally {
        Remove-Item -LiteralPath $multiSectionChangelog -ErrorAction SilentlyContinue
    }
}
