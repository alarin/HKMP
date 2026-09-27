<#
.SYNOPSIS
    Installs the HKMP skins listed in skins.json into the game's Mods\HKMP\Skins folder.

.DESCRIPTION
    Every entry in skins.json has a name and a fixed HKMP skin ID. The name is looked up in the HKSkins
    catalog (https://hkskins.art) to find where the skin is hosted, unless the entry has its own "source".
    Google Drive files (zip archives, including multi-skin packs) and Google Drive folders are downloaded
    automatically. The skin ends up in Skins\<name> with an id.txt, so every machine that runs this script
    with the same skins.json gets the same skin IDs.

    Skins from other hosts (Discord, Nexus, ...) cannot be downloaded automatically. Put them into
    Skins\<name> by hand and the script will still assign the ID from skins.json.

.PARAMETER SkinsDir
    The HKMP skins folder. Found through the Steam install when omitted.

.PARAMETER Force
    Download and reinstall skins even if they are already present.
#>
param(
    [string]$SkinsDir,
    [string]$Manifest = (Join-Path $PSScriptRoot 'skins.json'),
    [string]$CacheDir = (Join-Path $env:LOCALAPPDATA 'hkmp-skin-sync'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$CatalogUrl = 'https://hkskins.art/skins.zip'

function Find-SkinsDir {
    $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
    $libraries = @()
    if ($steam) {
        $libraries += $steam
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += $m.Groups[1].Value -replace '\\\\', '\'
            }
        }
    }
    $libraries += "${env:ProgramFiles(x86)}\Steam"

    foreach ($lib in $libraries) {
        $managed = Join-Path $lib 'steamapps\common\Hollow Knight\hollow_knight_Data\Managed'
        if (Test-Path $managed) {
            return Join-Path $managed 'Mods\HKMP\Skins'
        }
    }
    throw 'Could not find Hollow Knight in any Steam library, pass -SkinsDir'
}

function Normalize([string]$s) {
    return ($s.ToLowerInvariant() -replace '[^\p{L}\p{N}]', '')
}

function Get-Catalog {
    $zipPath = Join-Path $CacheDir 'hkskins-catalog.zip'
    $fresh = (Test-Path $zipPath) -and ((Get-Item $zipPath).LastWriteTime -gt (Get-Date).AddDays(-1))
    if (-not $fresh) {
        Write-Host 'Downloading HKSkins catalog...'
        Invoke-WebRequest -Uri $CatalogUrl -OutFile $zipPath -UseBasicParsing
    }

    $catalog = @{}
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.Name -ne 'metadata.json') { continue }
            $reader = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::UTF8)
            try {
                $meta = $reader.ReadToEnd() | ConvertFrom-Json
            } finally {
                $reader.Dispose()
            }
            $catalog[(Normalize $meta.name)] = $meta
        }
    } finally {
        $zip.Dispose()
    }
    return $catalog
}

function Get-DriveFile([string]$id) {
    $path = Join-Path $CacheDir "drive-file-$id"
    if (-not (Test-Path $path)) {
        Write-Host "  downloading Google Drive file $id..."
        $url = "https://drive.usercontent.google.com/download?id=$id&export=download&confirm=t"
        Invoke-WebRequest -Uri $url -OutFile "$path.part" -UseBasicParsing
        Move-Item "$path.part" $path
    }
    return $path
}

function Get-DriveFolder([string]$id, [string]$path) {
    # Assigned in every call, a nested call would otherwise see its caller's $done through dynamic scoping
    $done = $null
    if (-not $path) {
        $path = Join-Path $CacheDir "drive-folder-$id"
        if (Test-Path $path) { return $path }
        $done = $path
        $path = "$path.part"
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    New-Item -ItemType Directory -Force -Path $path | Out-Null

    Write-Host "  listing Google Drive folder $id..."
    $html = (Invoke-WebRequest -Uri "https://drive.google.com/embeddedfolderview?id=$id" -UseBasicParsing).Content
    $pattern = 'href="https://drive\.google\.com/(file/d|drive/folders)/([^/"?]+)[^"]*"[^>]*>.*?flip-entry-title">([^<]+)<'
    foreach ($m in [regex]::Matches($html, $pattern, 'Singleline')) {
        $kind = $m.Groups[1].Value
        $childId = $m.Groups[2].Value
        $name = [Net.WebUtility]::HtmlDecode($m.Groups[3].Value)
        $childPath = Join-Path $path $name
        if ($kind -eq 'drive/folders') {
            Get-DriveFolder $childId $childPath | Out-Null
        } else {
            Copy-Item (Get-DriveFile $childId) $childPath
        }
    }

    if ($done) {
        Move-Item $path $done
        return $done
    }
    return $path
}

# Downloads the source and returns either a zip file or a directory that contains the skin somewhere inside.
function Get-Source([string]$source) {
    if ($source -match 'drive\.google\.com/(?:u/\d+/)?(?:file/d/|open\?id=|uc\?(?:[^#]*&)?id=)([\w-]+)') {
        return Get-DriveFile $Matches[1]
    }
    if ($source -match 'drive\.google\.com/drive/(?:u/\d+/)?folders/([\w-]+)') {
        return Get-DriveFolder $Matches[1]
    }
    return $null
}

# Among all directories inside the source that contain a Knight.png, picks the one that belongs to this skin.
function Select-SkinDir([string[]]$dirs, [string[]]$names) {
    $dirs = @($dirs | Sort-Object -Unique)
    if ($dirs.Count -eq 1) { return $dirs[0] }

    $wanted = @($names | Where-Object { $_ } | ForEach-Object { Normalize $_ })
    foreach ($dir in $dirs) {
        if ($wanted -contains (Normalize (Split-Path $dir -Leaf))) { return $dir }
    }
    foreach ($dir in $dirs) {
        $leaf = Normalize (Split-Path $dir -Leaf)
        foreach ($w in $wanted) {
            if ($leaf -and ($leaf.Contains($w) -or $w.Contains($leaf))) { return $dir }
        }
    }
    throw "the source contains $($dirs.Count) skins, none of which matches the name; set ""folder"" in skins.json"
}

function Install-FromSource([string]$sourcePath, [string]$target, [string[]]$names) {
    $tmp = "$target.part"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null

    if (Test-Path $sourcePath -PathType Container) {
        $knights = Get-ChildItem $sourcePath -Recurse -File | Where-Object { $_.Name -ieq 'Knight.png' }
        if (-not $knights) { throw 'no Knight.png in the downloaded folder' }
        $dir = Select-SkinDir @($knights | ForEach-Object { $_.DirectoryName }) $names
        Get-ChildItem $dir -File -Filter *.png | Copy-Item -Destination $tmp
    } else {
        $zip = [IO.Compression.ZipFile]::OpenRead($sourcePath)
        try {
            $knights = $zip.Entries | Where-Object { $_.Name -ieq 'Knight.png' }
            if (-not $knights) { throw 'no Knight.png in the downloaded archive' }
            $dir = Select-SkinDir @($knights | ForEach-Object { $_.FullName.Substring(0, $_.FullName.Length - $_.Name.Length) }) $names
            foreach ($entry in $zip.Entries) {
                $parent = $entry.FullName.Substring(0, $entry.FullName.Length - $entry.Name.Length)
                if ($parent -eq $dir -and $entry.Name -like '*.png') {
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $tmp $entry.Name), $true)
                }
            }
        } finally {
            $zip.Dispose()
        }
    }

    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Move-Item $tmp $target
}

if (-not $SkinsDir) { $SkinsDir = Find-SkinsDir }
New-Item -ItemType Directory -Force -Path $SkinsDir, $CacheDir | Out-Null
Write-Host "Skins folder: $SkinsDir"

$skins = @((Get-Content $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json).skins)
$ids = @{}
foreach ($skin in $skins) {
    if (-not $skin.name -or $skin.name -match '[\\/:*?"<>|]') { throw "invalid skin name: '$($skin.name)'" }
    if ($skin.id -lt 1 -or $skin.id -gt 255) { throw "skin '$($skin.name)': id must be between 1 and 255" }
    if ($ids.ContainsKey([int]$skin.id)) { throw "skins '$($ids[[int]$skin.id])' and '$($skin.name)' share id $($skin.id)" }
    $ids[[int]$skin.id] = $skin.name
}

$catalog = $null
$failed = @()
foreach ($skin in $skins) {
    Write-Host "[$($skin.id)] $($skin.name)"
    $target = Join-Path $SkinsDir $skin.name
    try {
        $installed = Get-ChildItem $target -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -ieq 'Knight.png' }
        if ($Force -or -not $installed) {
            $source = $skin.source
            $meta = $null
            if (-not $source) {
                if (-not $catalog) { $catalog = Get-Catalog }
                $meta = $catalog[(Normalize $skin.name)]
                if (-not $meta) { throw 'not found in the HKSkins catalog; add "source" to skins.json' }
                $source = $meta.source
            }
            $sourcePath = Get-Source $source
            if (-not $sourcePath) {
                throw "cannot download automatically from $source; put the skin into $target by hand"
            }
            Install-FromSource $sourcePath $target @($skin.folder, $skin.name, $meta.name)
            Write-Host '  installed'
        } else {
            Write-Host '  already installed'
        }
        Set-Content -Path (Join-Path $target 'id.txt') -Value $skin.id -NoNewline -Encoding ASCII
    } catch {
        Write-Warning "  $($skin.name): $($_.Exception.Message)"
        $failed += $skin.name
    }
}

# HKMP only loads one skin per ID, so a skin outside skins.json with a clashing id.txt would shadow ours
$managed = @($skins | ForEach-Object { $_.name })
foreach ($dir in Get-ChildItem $SkinsDir -Directory) {
    if ($managed -contains $dir.Name) { continue }
    $idFile = Join-Path $dir.FullName 'id.txt'
    if (Test-Path $idFile) {
        $id = 0
        if ([int]::TryParse((Get-Content $idFile -Raw), [ref]$id) -and $ids.ContainsKey($id)) {
            Write-Warning "'$($dir.Name)' is not in skins.json but uses id $id of '$($ids[$id])'"
        }
    }
}

if ($failed) {
    Write-Warning "Failed: $($failed -join ', ')"
    exit 1
}
Write-Host 'All skins are in place. Restart the game to load them.'
