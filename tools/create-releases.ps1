$ErrorActionPreference = 'Stop'

$Repo = 'wil114/autopushlglr'
$RequiredUser = 'wil114'
$Root = Split-Path -Parent $PSScriptRoot
$InstallerRoot = Join-Path $Root 'installers'
$Versions = @('v1.0.0','v1.0.1','v1.0.2','v1.0.3','v1.0.4','v1.0.5','v1.0.6')

function Set-ProxyFromWindows {
    $settings = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    if ($settings.ProxyEnable -eq 1 -and $settings.ProxyServer) {
        $proxy = $settings.ProxyServer
        if ($proxy -notmatch '^https?://') {
            $proxy = "http://$proxy"
        }
        $env:HTTP_PROXY = $proxy
        $env:HTTPS_PROXY = $proxy
        Write-Host "Using system proxy: $proxy"
    }
}

function Require-Command($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Command not found: $name"
    }
}

Set-ProxyFromWindows
Require-Command gh

gh auth status --hostname github.com *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'GitHub CLI is not logged in. Starting browser login.'
    gh auth login --hostname github.com --web --git-protocol https --scopes repo
}

$Login = gh api user --jq .login
if ($Login -ne $RequiredUser) {
    throw "Current GitHub user is $Login, expected $RequiredUser. Stopped."
}

gh repo view $Repo *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Repository not found: $Repo"
}

foreach ($version in $Versions) {
    $asset = Join-Path $InstallerRoot "$version\QQMonitorSetup-$version.exe"
    $notes = Get-ChildItem -LiteralPath (Join-Path $InstallerRoot $version) -File -Filter '*.txt' | Select-Object -First 1 -ExpandProperty FullName
    if (-not (Test-Path -LiteralPath $asset)) {
        throw "Missing installer: $asset"
    }
    if (-not $notes) {
        throw "Missing release notes for $version"
    }

    gh release view $version --repo $Repo *> $null
    if ($LASTEXITCODE -eq 0) {
        gh release edit $version --repo $Repo --title $version --notes-file $notes
        gh release upload $version $asset --repo $Repo --clobber
        Write-Host "Updated release: $version"
    } else {
        gh release create $version $asset --repo $Repo --target main --title $version --notes-file $notes
        Write-Host "Created release: $version"
    }
}

Write-Host ''
Write-Host 'Done. Download page:'
Write-Host "https://github.com/$Repo/releases"
