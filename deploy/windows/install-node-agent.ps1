# Reproducible Windows install for the Chorus NodeAgent (M0 install config).
# Creates the restricted service account, state directory ACL and Windows service.
# Runtime acceptance (service start, Job Object child reclaim, certificate store)
# requires a Windows host and is recorded NOT RUN until executed there.
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$ServiceAccount = "chorus-agent",
    [string]$StateDir = "C:\ProgramData\chorus\agent",
    [string]$InstallRoot = "C:\Program Files\chorus\node-agent",
    [string]$EnvFile = "C:\ProgramData\chorus\node-agent.env",
    [string]$EnvTemplate = "" # operator-provided; copied with a restricted ACL, never displayed
)

$ErrorActionPreference = "Stop"
function Step([string]$Name, [scriptblock]$Action) {
    try { & $Action; Write-Output "[OK] $Name" }
    catch { Write-Error "[FAIL] $Name : $_"; exit 1 }
}

Step "publish Chorus.NodeAgent (win-x64)" {
    dotnet publish "$RepoRoot\src\Chorus.NodeAgent\Chorus.NodeAgent.csproj" `
        -c Release -r win-x64 --self-contained false -o $InstallRoot --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

Step "service account $ServiceAccount present with service-logon password (never displayed)" {
    if (-not (Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue)) {
        New-LocalUser -Name $ServiceAccount -AccountNeverExpires `
            -Description "Chorus NodeAgent service account" | Out-Null
    }
    # A service account needs a non-empty password to log on; it is generated here,
    # handed only to the Service Control Manager and never written to disk or output.
    $script:servicePassword = ConvertTo-SecureString ([Guid]::NewGuid().ToString("N") +
        ([Guid]::NewGuid().ToString("N"))) -AsPlainText -Force
    Set-LocalUser -Name $ServiceAccount -Password $script:servicePassword -PasswordNeverExpires $true
}

Step "state directory $StateDir with service-account-only ACL" {
    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
    $acl = Get-Acl $StateDir
    $acl.SetAccessRuleProtection($true, $false)
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $ServiceAccount, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($rule)
    Set-Acl $StateDir $acl
}

Step "environment file $EnvFile with restricted ACL" {
    if ($EnvTemplate -ne "") {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $EnvFile) | Out-Null
        Copy-Item $EnvTemplate $EnvFile -Force
        icacls $EnvFile /inheritance:r /grant:r "${ServiceAccount}:R" | Out-Null
    } else {
        Write-Output "[SKIP] no -EnvTemplate given; create $EnvFile (service account read-only) before starting"
    }
}

Step "Windows service chorus-node-agent registered" {
    $dotnet = (Get-Command dotnet).Source
    if (Get-Service -Name chorus-node-agent -ErrorAction SilentlyContinue) {
        Set-Service -Name chorus-node-agent -StartupType Automatic
    } else {
        New-Service -Name chorus-node-agent -DisplayName "Chorus NodeAgent" `
            -BinaryPathName "`"$dotnet`" `"$InstallRoot\Chorus.NodeAgent.dll`"" `
            -StartupType Automatic -Credential (
                New-Object System.Management.Automation.PSCredential(".\$ServiceAccount", $script:servicePassword)) | Out-Null
    }
    sc.exe failure chorus-node-agent reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}

Write-Output "Install complete. Start with: Start-Service chorus-node-agent"
Write-Output "NOT RUN: service start, Job Object reclaim and certificate store behaviour need Windows host acceptance (AT29)."
