[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [Alias('InstallDir', 'i')]
    [string] $InstallRoot,

    [Alias('b')]
    [string] $BinDir,

    [Alias('v')]
    [string] $Version,

    [switch] $Prerelease,

    [switch] $NoPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Repository = 'rafael-vasconcellos/VNTextPatch-net8'
$script:ApiBase = "https://api.github.com/repos/$($script:Repository)/releases"
$script:ExecutableName = 'VNTextPatch.exe'
$script:LauncherName = 'VNTextPatch.cmd'
$script:UserAgent = 'VNTextPatch-PowerShell-Installer/1.0'
$script:LauncherMarker = 'REM Managed by VNTextPatch installer'
$script:MaxRetries = 3
$script:RequestTimeoutMs = 30000
$script:ChunkSize = 1048576

# Windows PowerShell 5.1 pode iniciar com TLS 1.0; o GitHub exige TLS moderno.
try {
    if ([Enum]::GetNames([Net.SecurityProtocolType]) -contains 'Tls12') {
        [Net.ServicePointManager]::SecurityProtocol = ([Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12)
    }
}
catch { }

function Stop-Install {
    param([Parameter(Mandatory)][string] $Message)
    throw [System.InvalidOperationException]::new($Message)
}

function Resolve-UserPath {
    param([Parameter(Mandatory)][string] $Value)

    $expanded = [Environment]::ExpandEnvironmentVariables($Value)
    if ($expanded -eq '~') {
        $expanded = $HOME
    }
    elseif ($expanded.StartsWith('~\') -or $expanded.StartsWith('~/')) {
        $expanded = Join-Path $HOME $expanded.Substring(2)
    }

    return [IO.Path]::GetFullPath($expanded)
}

function Assert-AllowedUri {
    param(
        [Parameter(Mandatory)][Uri] $Uri,
        [Parameter(Mandatory)][string[]] $AllowedHosts
    )

    if (
        $Uri.Scheme -cne 'https' -or
        $Uri.Port -ne 443 -or
        $Uri.UserInfo -or
        $Uri.Host -notin $AllowedHosts
    ) {
        Stop-Install "HTTPS address not allowed: $($Uri.GetLeftPart([UriPartial]::Authority))"
    }
}

function Get-ValidatedHttpResponse {
    param(
        [Parameter(Mandatory)][Uri] $Uri,
        [Parameter(Mandatory)][string[]] $AllowedHosts,
        [string] $Accept = 'application/octet-stream',
        [hashtable] $Headers = @{}
    )

    for ($attempt = 0; $attempt -le $script:MaxRetries; $attempt++) {
        $currentUri = $Uri
        $retryRequest = $false

        for ($redirect = 0; $redirect -le 5; $redirect++) {
            Assert-AllowedUri -Uri $currentUri -AllowedHosts $AllowedHosts

            $request = [Net.HttpWebRequest] [Net.WebRequest]::Create($currentUri)
            $request.Method = 'GET'
            $request.UserAgent = $script:UserAgent
            $request.Accept = $Accept
            $request.AllowAutoRedirect = $false
            $request.Timeout = $script:RequestTimeoutMs
            $request.ReadWriteTimeout = $script:RequestTimeoutMs
            $request.KeepAlive = $false

            foreach ($headerName in $Headers.Keys) {
                $request.Headers[[string] $headerName] = [string] $Headers[$headerName]
            }

            $response = $null
            try {
                $response = $request.GetResponse()
            }
            catch [Net.WebException] {
                if ($_.Exception.Response) {
                    $response = [Net.HttpWebResponse] $_.Exception.Response
                }
                else {
                    if ($attempt -lt $script:MaxRetries) {
                        Start-Sleep -Seconds ($attempt + 1)
                        $retryRequest = $true
                        break
                    }
                    Stop-Install "HTTPS request failed: $($_.Exception.Message)"
                }
            }

            $statusCode = [int] $response.StatusCode
            if ($statusCode -ge 300 -and $statusCode -lt 400) {
                $location = $response.Headers['Location']
                $response.Close()
                if (-not $location) {
                    Stop-Install "The server returned HTTP $statusCode without a redirect location."
                }
                if ($redirect -ge 5) {
                    Stop-Install 'The download exceeded the redirect limit.'
                }
                $currentUri = [Uri]::new($currentUri, $location)
                continue
            }

            if ($statusCode -ge 400) {
                $response.Close()
                $retryable = $statusCode -in @(408, 425, 429, 500, 502, 503, 504)
                if ($retryable -and $attempt -lt $script:MaxRetries) {
                    Start-Sleep -Seconds ($attempt + 1)
                    $retryRequest = $true
                    break
                }
                Stop-Install "The GitHub request failed (HTTP $statusCode)."
            }

            return $response
        }

        if (-not $retryRequest) {
            Stop-Install 'Could not complete the HTTPS request.'
        }
    }

    Stop-Install 'The HTTPS request failed after several attempts.'
}

function Get-ResponseText {
    param([Parameter(Mandatory)] $Response)

    $stream = $null
    $reader = $null
    try {
        $stream = $Response.GetResponseStream()
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
        return $reader.ReadToEnd()
    }
    finally {
        if ($reader) { $reader.Dispose() }
        elseif ($stream) { $stream.Dispose() }
        $Response.Close()
    }
}

function Get-GitHubRelease {
    param(
        [string] $RequestedVersion,
        [switch] $IncludePrerelease
    )

    if ($RequestedVersion) {
        $endpoint = "$($script:ApiBase)/tags/$([Uri]::EscapeDataString($RequestedVersion))"
    }
    elseif ($IncludePrerelease) {
        # /releases/latest ignores pre-releases, so list releases and pick the newest one.
        $endpoint = "$($script:ApiBase)?per_page=30"
    }
    else {
        $endpoint = "$($script:ApiBase)/latest"
    }

    try {
        $response = Get-ValidatedHttpResponse `
            -Uri ([Uri] $endpoint) `
            -AllowedHosts @('api.github.com') `
            -Accept 'application/vnd.github+json' `
            -Headers @{ 'X-GitHub-Api-Version' = '2022-11-28' }
        $json = Get-ResponseText -Response $response
        $release = ConvertFrom-Json -InputObject $json
        if ($IncludePrerelease -and -not $RequestedVersion) {
            $release = $release | Where-Object { $_ -and -not $_.draft } | Select-Object -First 1
            if (-not $release) {
                Stop-Install 'No releases were found on GitHub.'
            }
        }
    }
    catch {
        if ($RequestedVersion -and $_.Exception.Message -match 'HTTP 404') {
            Stop-Install "Version '$RequestedVersion' was not found on GitHub."
        }
        throw
    }

    if (-not $release -or $release.tag_name -isnot [string]) {
        Stop-Install 'The GitHub API returned invalid release metadata.'
    }
    if ($release.tag_name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        Stop-Install 'The API returned an invalid version tag.'
    }
    if ($RequestedVersion -and $release.tag_name -cne $RequestedVersion) {
        Stop-Install "Version $RequestedVersion was requested, but GitHub returned $($release.tag_name)."
    }

    return $release
}

function Assert-AssetUrl {
    param(
        [Parameter(Mandatory)][string] $Url,
        [Parameter(Mandatory)][string] $Tag,
        [Parameter(Mandatory)][string] $AssetName
    )

    try { $parsed = [Uri]::new($Url) }
    catch { Stop-Install 'The API returned an invalid download URL.' }

    $expectedPath = "/$($script:Repository)/releases/download/$Tag/$AssetName"
    if (
        $parsed.Scheme -cne 'https' -or
        $parsed.Host -cne 'github.com' -or
        $parsed.Port -ne 443 -or
        $parsed.UserInfo -or
        $parsed.Query -or
        $parsed.Fragment -or
        [Uri]::UnescapeDataString($parsed.AbsolutePath) -cne $expectedPath
    ) {
        Stop-Install 'The asset URL does not match the expected official GitHub address.'
    }
}

function Get-ReleaseAsset {
    param(
        [Parameter(Mandatory)] $Release,
        [Parameter(Mandatory)][string] $Tag
    )

    $assetName = "VNTextPatch-win-x64-$Tag.zip"
    $asset = @($Release.assets) | Where-Object { $_.name -ceq $assetName } | Select-Object -First 1
    if (-not $asset) {
        $available = @($Release.assets | ForEach-Object { $_.name } | Where-Object { $_ }) -join ', '
        if (-not $available) { $available = 'no assets' }
        Stop-Install "Release $Tag does not contain $assetName.`nPublished assets: $available"
    }
    if (-not $asset.browser_download_url) {
        Stop-Install "Release $Tag has no download URL for $assetName."
    }

    $checksumName = "$assetName.sha256"
    $checksumAsset = @($Release.assets) | Where-Object { $_.name -ceq $checksumName } | Select-Object -First 1
    $checksumUrl = if ($checksumAsset) { [string] $checksumAsset.browser_download_url } else { $null }

    return [PSCustomObject] @{
        Name        = $assetName
        Url         = [string] $asset.browser_download_url
        Digest      = [string] $asset.digest
        ChecksumUrl = $checksumUrl
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)

    $stream = $null
    $sha = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        $sha = [Security.Cryptography.SHA256]::Create()
        $hash = $sha.ComputeHash($stream)
        return ([BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        if ($sha) { $sha.Dispose() }
        if ($stream) { $stream.Dispose() }
    }
}

function Save-RemoteFile {
    param(
        [Parameter(Mandatory)][string] $Url,
        [Parameter(Mandatory)][string] $Destination
    )

    $allowedHosts = @(
        'github.com',
        'release-assets.githubusercontent.com',
        'objects.githubusercontent.com'
    )

    for ($attempt = 0; $attempt -le $script:MaxRetries; $attempt++) {
        $response = $null
        $inputStream = $null
        $outputStream = $null
        try {
            $response = Get-ValidatedHttpResponse `
                -Uri ([Uri] $Url) `
                -AllowedHosts $allowedHosts `
                -Accept 'application/octet-stream'
            $inputStream = $response.GetResponseStream()
            $outputStream = [IO.File]::Open(
                $Destination,
                [IO.FileMode]::Create,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None
            )

            $buffer = New-Object byte[] $script:ChunkSize
            while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $outputStream.Write($buffer, 0, $read)
            }
            return
        }
        catch {
            if (Test-Path -LiteralPath $Destination) {
                Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
            }
            if ($attempt -ge $script:MaxRetries) {
                Stop-Install "Download failed after $($script:MaxRetries + 1) attempts: $($_.Exception.Message)"
            }
            Start-Sleep -Seconds ($attempt + 1)
        }
        finally {
            if ($outputStream) { $outputStream.Dispose() }
            if ($inputStream) { $inputStream.Dispose() }
            if ($response) { $response.Close() }
        }
    }
}

function Get-ExpectedChecksum {
    param([Parameter(Mandatory)][string] $Path)

    try { $content = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) }
    catch { Stop-Install "Could not read the checksum file: $($_.Exception.Message)" }

    $matches = [regex]::Matches($content, '(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])')
    if ($matches.Count -ne 1) {
        Stop-Install 'The checksum file must contain exactly one SHA-256 hash.'
    }
    return $matches[0].Value.ToLowerInvariant()
}

function Expand-ValidatedArchive {
    param(
        [Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $ExpectedRoot
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = $null
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $foundExecutable = $false
    $expectedExecutable = "$ExpectedRoot/$($script:ExecutableName)"
    $destinationFull = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'

    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
        if ($archive.Entries.Count -eq 0) {
            Stop-Install 'The ZIP file is empty.'
        }

        foreach ($entry in $archive.Entries) {
            $name = [string] $entry.FullName
            if (
                [string]::IsNullOrWhiteSpace($name) -or
                $name.Contains('\') -or
                $name.Contains([char] 0) -or
                $name.StartsWith('/') -or
                $name -match '^[A-Za-z]:'
            ) {
                Stop-Install "Unsafe path in ZIP: '$name'."
            }

            $isDirectory = $name.EndsWith('/')
            $normalizedName = if ($isDirectory) { $name.TrimEnd('/') } else { $name }
            $parts = $normalizedName.Split('/')
            if (
                $parts.Count -eq 0 -or
                $parts[0] -cne $ExpectedRoot -or
                @($parts | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.Contains(':') }).Count -gt 0
            ) {
                Stop-Install "Entry outside the expected folder in ZIP: '$name'."
            }
            if (-not $seen.Add($normalizedName)) {
                Stop-Install "The ZIP contains a duplicate entry: '$name'."
            }

            $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xFFFF
            $fileType = $unixMode -band 0xF000
            if ($fileType -eq 0xA000) {
                Stop-Install "Symbolic links are not allowed in ZIP: '$name'."
            }
            if ($fileType -notin @(0, 0x8000, 0x4000)) {
                Stop-Install "Special file type not allowed in ZIP: '$name'."
            }
            if (-not $isDirectory -and $normalizedName -ceq $expectedExecutable) {
                $foundExecutable = $true
            }
        }

        if (-not $foundExecutable) {
            Stop-Install "The ZIP does not contain $expectedExecutable."
        }

        foreach ($entry in $archive.Entries) {
            $name = [string] $entry.FullName
            $isDirectory = $name.EndsWith('/')
            $normalizedName = if ($isDirectory) { $name.TrimEnd('/') } else { $name }
            $parts = $normalizedName.Split('/')
            $relativePath = [IO.Path]::Combine([string[]] $parts)
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $relativePath))
            if (-not $target.StartsWith($destinationFull, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Install "Entry outside the extraction directory: '$name'."
            }

            if ($isDirectory) {
                [void] [IO.Directory]::CreateDirectory($target)
                continue
            }

            [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            $sourceStream = $null
            $targetStream = $null
            try {
                $sourceStream = $entry.Open()
                $targetStream = [IO.File]::Open(
                    $target,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None
                )
                $sourceStream.CopyTo($targetStream, $script:ChunkSize)
            }
            finally {
                if ($targetStream) { $targetStream.Dispose() }
                if ($sourceStream) { $sourceStream.Dispose() }
            }
        }
    }
    catch [System.IO.InvalidDataException] {
        Stop-Install "The downloaded file is not a valid ZIP: $($_.Exception.Message)"
    }
    finally {
        if ($archive) { $archive.Dispose() }
    }

    $publishedDir = Join-Path $Destination $ExpectedRoot
    $executablePath = Join-Path $publishedDir $script:ExecutableName
    if (-not (Test-Path -LiteralPath $publishedDir -PathType Container)) {
        Stop-Install "The ZIP does not contain the folder $ExpectedRoot."
    }
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        Stop-Install 'The Windows executable was not found after extraction.'
    }

    $attributes = [IO.File]::GetAttributes($executablePath)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Stop-Install 'The extracted executable cannot be a link or reparse point.'
    }

    return $executablePath
}

function Test-ManagedLauncher {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return }
    $launcherAttributes = [IO.File]::GetAttributes($Path)
    if (($launcherAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Stop-Install "$Path is an existing link or reparse point. It will not be overwritten."
    }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        Stop-Install "$Path already exists and is a directory."
    }

    try { $content = [IO.File]::ReadAllText($Path) }
    catch { Stop-Install "A file already exists at $Path and it cannot be verified." }

    $expectedPrefix = "@echo off`r`n$($script:LauncherMarker)"
    if (-not $content.StartsWith($expectedPrefix, [StringComparison]::Ordinal)) {
        Stop-Install "$Path already exists and does not appear to belong to this installation. It will not be overwritten."
    }
}

function Set-Launcher {
    param(
        [Parameter(Mandatory)][string] $BinDirectory,
        [Parameter(Mandatory)][string] $ExecutablePath
    )

    $launcher = Join-Path $BinDirectory $script:LauncherName
    Test-ManagedLauncher -Path $launcher

    # Escapa % para impedir expansão acidental de variáveis pelo cmd.exe.
    $target = [IO.Path]::GetFullPath($ExecutablePath).Replace('%', '%%')
    $content = "@echo off`r`n$($script:LauncherMarker)`r`n`"$target`" %*`r`n"
    $temporary = Join-Path $BinDirectory ('.VNTextPatch-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $encoding = [Text.UTF8Encoding]::new($false)

    try {
        [IO.File]::WriteAllText($temporary, $content, $encoding)
        if (Test-Path -LiteralPath $launcher) {
            [IO.File]::Replace($temporary, $launcher, [NullString]::Value)
        }
        else {
            [IO.File]::Move($temporary, $launcher)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }

    return $launcher
}

function Test-PathContainsDirectory {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [string] $PathValue
    )

    if ($null -eq $PathValue) { $PathValue = $env:Path }
    $target = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    foreach ($item in ($PathValue -split ';')) {
        if ([string]::IsNullOrWhiteSpace($item)) { continue }
        $candidate = [Environment]::ExpandEnvironmentVariables($item.Trim().Trim('"'))
        try { $candidate = [IO.Path]::GetFullPath($candidate).TrimEnd('\') }
        catch { continue }
        if ([string]::Equals($candidate, $target, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Send-UserEnvironmentChange {
    # Notifica o Explorer e outros processos de que o ambiente do usuário mudou.
    try {
        if (-not ('VntpInstaller.NativeMethods' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace VntpInstaller {
    public static class NativeMethods {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
            uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
    }
}
'@ | Out-Null
        }
        $result = [UIntPtr]::Zero
        [void] [VntpInstaller.NativeMethods]::SendMessageTimeout(
            [IntPtr] 0xffff, 0x001A, [UIntPtr]::Zero,
            'Environment', 0x0002, 5000, [ref] $result
        )
    }
    catch {
        # A gravação em HKCU já ocorreu; o broadcast é apenas uma conveniência.
    }
}

function Add-UserPath {
    param([Parameter(Mandatory)][string] $Directory)

    $currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $currentUserPath) { $currentUserPath = '' }
    if (Test-PathContainsDirectory -Directory $Directory -PathValue $currentUserPath) {
        if (-not (Test-PathContainsDirectory -Directory $Directory -PathValue $env:Path)) {
            $env:Path = "$Directory;$env:Path"
        }
        return 'The VNTextPatch directory is already in the user PATH.'
    }

    $newPath = if ([string]::IsNullOrWhiteSpace($currentUserPath)) {
        $Directory
    }
    else {
        $currentUserPath.TrimEnd(';') + ';' + $Directory
    }

    try {
        # EnvironmentVariableTarget.User grava em HKCU\Environment; não exige administrador.
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Send-UserEnvironmentChange
        if (-not (Test-PathContainsDirectory -Directory $Directory -PathValue $env:Path)) {
            $env:Path = "$Directory;$env:Path"
        }
        return 'User PATH updated. Open a new terminal to use VNTextPatch.'
    }
    catch {
        return "Installation completed, but the user PATH could not be updated: $($_.Exception.Message)`nAdd this directory to your PATH manually: $Directory"
    }
}

function Get-DefaultDirectories {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        $localAppData = Join-Path $HOME 'AppData\Local'
    }
    $root = Join-Path $localAppData 'Programs\VNTextPatch'
    return [PSCustomObject] @{ Root = $root; Bin = (Join-Path $root 'bin') }
}

function Invoke-Installer {
    $architecture = $env:PROCESSOR_ARCHITEW6432
    if (-not $architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    if ($architecture -notin @('AMD64', 'x86_64')) {
        Stop-Install "This release is win-x64, but the detected architecture is '$architecture'."
    }

    if ($Prerelease -and $Version) {
        Stop-Install '-Prerelease cannot be combined with -Version.'
    }
    if ($Version -and $Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        Stop-Install "Invalid version tag: $Version"
    }

    $defaults = Get-DefaultDirectories
    $rootValue = if ($InstallRoot) { $InstallRoot } elseif ($env:VNTP_INSTALL_ROOT) { $env:VNTP_INSTALL_ROOT } else { $defaults.Root }
    $binValue = if ($BinDir) { $BinDir } elseif ($env:VNTP_BIN_DIR) { $env:VNTP_BIN_DIR } else { $defaults.Bin }
    $root = Resolve-UserPath -Value $rootValue
    $bin = Resolve-UserPath -Value $binValue

    [void] [IO.Directory]::CreateDirectory($root)
    [void] [IO.Directory]::CreateDirectory($bin)
    $root = [IO.Path]::GetFullPath($root)
    $bin = [IO.Path]::GetFullPath($bin)

    Write-Host 'Detected platform: win-x64'
    Write-Host 'Querying GitHub releases...'
    $release = Get-GitHubRelease -RequestedVersion $Version -IncludePrerelease:$Prerelease
    $tag = [string] $release.tag_name
    $asset = Get-ReleaseAsset -Release $release -Tag $tag
    Assert-AssetUrl -Url $asset.Url -Tag $tag -AssetName $asset.Name

    $expectedDigest = $null
    if ($asset.Digest -match '(?i)^sha256:([0-9a-f]{64})$') {
        $expectedDigest = $Matches[1].ToLowerInvariant()
    }
    elseif (-not $asset.ChecksumUrl) {
        Stop-Install "GitHub did not provide the SHA-256 digest for $($asset.Name) nor a .sha256 sidecar asset. Nothing was installed."
    }
    elseif ($asset.ChecksumUrl) {
        Assert-AssetUrl -Url $asset.ChecksumUrl -Tag $tag -AssetName ($asset.Name + '.sha256')
    }

    $versionDirectory = Join-Path $root "VNTextPatch-$tag"
    $launcher = Join-Path $bin $script:LauncherName
    Test-ManagedLauncher -Path $launcher

    if (Test-Path -LiteralPath $versionDirectory) {
        $versionAttributes = [IO.File]::GetAttributes($versionDirectory)
        if (($versionAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Stop-Install "The directory for version $tag is a link or reparse point."
        }
        $existingExe = Join-Path $versionDirectory $script:ExecutableName
        if (
            -not (Test-Path -LiteralPath $versionDirectory -PathType Container) -or
            -not (Test-Path -LiteralPath $existingExe -PathType Leaf)
        ) {
            Stop-Install "A directory for $tag already exists, but it does not contain a valid installation: $versionDirectory"
        }
        $existingAttributes = [IO.File]::GetAttributes($existingExe)
        if (($existingAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Stop-Install "The existing executable for $tag is not a regular file."
        }

        $launcher = Set-Launcher -BinDirectory $bin -ExecutablePath $existingExe
        Write-Host "VNTextPatch $tag was already installed."
        Write-Host "Command: $launcher"
        if (-not $NoPath) { Write-Host (Add-UserPath -Directory $bin) }
        return
    }

    $staging = Join-Path $root ('.VNTextPatch-install-' + [Guid]::NewGuid().ToString('N'))
    $published = $false
    $committed = $false
    try {
        [void] [IO.Directory]::CreateDirectory($staging)
        $extractDirectory = Join-Path $staging 'extracted'
        [void] [IO.Directory]::CreateDirectory($extractDirectory)
        $archivePath = Join-Path $staging $asset.Name

        if (-not $expectedDigest) {
            $checksumPath = Join-Path $staging ($asset.Name + '.sha256')
            Write-Host 'Downloading SHA-256 checksum...'
            Save-RemoteFile -Url $asset.ChecksumUrl -Destination $checksumPath
            $expectedDigest = Get-ExpectedChecksum -Path $checksumPath
        }

        Write-Host "Downloading VNTextPatch $tag ($($asset.Name))..."
        Save-RemoteFile -Url $asset.Url -Destination $archivePath
        $actualDigest = Get-Sha256 -Path $archivePath
        if ($actualDigest -cne $expectedDigest) {
            Stop-Install "SHA-256 verification failed. Expected: $expectedDigest; got: $actualDigest."
        }
        Write-Host 'SHA-256 verified.'

        $expectedRoot = 'VNTextPatch-win-x64'
        [void] (Expand-ValidatedArchive -ArchivePath $archivePath -Destination $extractDirectory -ExpectedRoot $expectedRoot)
        $extractedRoot = Join-Path $extractDirectory $expectedRoot

        # O staging fica dentro da raiz final; o rename ocorre no mesmo volume.
        [IO.Directory]::Move($extractedRoot, $versionDirectory)
        $published = $true
        $executablePath = Join-Path $versionDirectory $script:ExecutableName

        # Só troca o launcher após download, hash, validação e publicação da versão.
        $launcher = Set-Launcher -BinDirectory $bin -ExecutablePath $executablePath
        $committed = $true

        Write-Host "VNTextPatch $tag installed successfully."
        Write-Host "Version: $versionDirectory"
        Write-Host "Command: $launcher"
        if (-not $NoPath) { Write-Host (Add-UserPath -Directory $bin) }
        Write-Host 'Run: VNTextPatch --help'
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($published -and -not $committed -and (Test-Path -LiteralPath $versionDirectory)) {
            Remove-Item -LiteralPath $versionDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    Invoke-Installer
}
catch {
    [Console]::Error.WriteLine("Error: $($_.Exception.Message)")
    throw $_.Exception
}
