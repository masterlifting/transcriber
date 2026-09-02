# Builds the portable Windows distribution (Full) for Transcriber.
#
#   .\build-portable.ps1
#
# Requirements (build machine only):
#   - .NET SDK 10 (dotnet on PATH)
#   - Python 3.12 venv with pip (used only to download pinned wheels)
#   - internet access (downloads: .NET runtime pack via NuGet, Python embeddable,
#     pinned wheels from PyPI)
# The produced ZIP is self-contained and needs no internet at runtime.

$ErrorActionPreference = "Stop"

$Root       = "D:\transcriber"
$Artifacts  = "$Root\artifacts\portable\win-x64"
$Version    = "1.0.0"
$PythonVer  = "3.12.10"
$ModelRepo  = "mobiuslabsgmbh/faster-whisper-large-v3-turbo"
$ModelRev   = "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf"

$dotnet  = "dotnet"
$py      = "$Root\.venv\Scripts\python.exe"

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body
}

function New-CleanDir([string]$Path) {
    if (Test-Path $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Expand-Zip([string]$Zip, [string]$Dest) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Dest)
}

function Get-WheelLicenseFiles([string]$SitePackages, [string]$LicensesDir) {
    # Copy license texts from each vendored package's .dist-info into licenses\<pkg>\
    Get-ChildItem $SitePackages -Directory -Filter "*.dist-info" | ForEach-Object {
        $pkgName = $_.Name -replace "\.dist-info$", ""
        $pkgLic = Join-Path $LicensesDir $pkgName
        New-Item -ItemType Directory -Path $pkgLic -Force | Out-Null
        Get-ChildItem $_.FullName -File | Where-Object {
            $_.Name -match "^(LICENSE|LICENCE|COPYING|NOTICE|AUTHORS)(\.|$)" -or
            ($_.Name -eq "METADATA")
        } | ForEach-Object { Copy-Item $_.FullName $pkgLic }
    }
}

function Build-Variant([string]$Variant) {
    Write-Host ""
    Write-Host "========== Building $Variant ==========" -ForegroundColor Green
    $staging = "$Artifacts\$Variant"
    New-CleanDir $staging
    New-Item -ItemType Directory -Path "$staging\python" -Force | Out-Null

    # ---- 1. .NET self-contained publish (all three exes share the staging root) ----
    Invoke-Step "Publish Desktop (self-contained win-x64)" {
        & $dotnet publish "$Root\recorder\src\Desktop\Transcriber.Desktop.csproj" `
            -c Release -r win-x64 --self-contained true -o $staging | Out-Null
    }
    Invoke-Step "Publish Recorder (self-contained win-x64)" {
        & $dotnet publish "$Root\recorder\src\Recorder\Recorder.csproj" `
            -c Release -r win-x64 --self-contained true -o $staging | Out-Null
    }
    Invoke-Step "Publish CLI (self-contained win-x64)" {
        & $dotnet publish "$Root\recorder\src\Cli\Cli.csproj" `
            -c Release -r win-x64 --self-contained true -o $staging | Out-Null
    }

    # ---- 2. Private Python runtime (official embeddable distribution) ----
    Invoke-Step "Download embeddable Python $PythonVer" {
        $zip = "$env:TEMP\python-$PythonVer-embed-amd64.zip"
        if (-not (Test-Path $zip)) {
            Invoke-WebRequest -Uri "https://www.python.org/ftp/python/$PythonVer/python-$PythonVer-embed-amd64.zip" `
                -OutFile $zip
        }
        Expand-Zip $zip "$staging\python"
        # configure search path: stdlib zip + Lib + site-packages + AppRoot
        # (AppRoot ("..") is the parent that contains the "pipeline" package)
        @"
python312.zip
.
Lib
Lib\site-packages
..
"@ | Set-Content "$staging\python\python312._pth" -Encoding ASCII
        # embeddable ships its own PSF license file when present; keep it
        $pyLicDir = "$staging\licenses\python-embeddable"
        New-Item -ItemType Directory -Path $pyLicDir -Force | Out-Null
        if (Test-Path "$staging\python\LICENSE.txt") { Copy-Item "$staging\python\LICENSE.txt" "$pyLicDir\LICENSE.txt" }
    }

    # ---- 3. Vendor pinned Python packages (no runtime pip) ----
    Invoke-Step "Vendor pinned Python packages" {
        $req = "$env:TEMP\lt-python-requirements.txt"
        & $py -m pip freeze | Out-File $req -Encoding utf8
        $wheels = "$env:TEMP\lt-wheels"
        New-CleanDir $wheels
        & $py -m pip download -r $req `
            --only-binary=:all: `
            --platform win_amd64 --python-version 3.12 --implementation cp `
            -d $wheels | Out-Null
        $sitePackages = "$staging\python\Lib\site-packages"
        New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null
        Get-ChildItem $wheels -Filter *.whl | ForEach-Object {
            Expand-Zip $_.FullName $sitePackages
        }
        Copy-Item $req "$staging\python-packages.txt"
    }

    # ---- 4. Pipeline (runtime files only; dev-only tooling excluded) ----
    Invoke-Step "Copy pipeline" {
        New-Item -ItemType Directory -Path "$staging\pipeline" -Force | Out-Null
        Get-ChildItem "$Root\pipeline" -File -Filter *.py |
            Where-Object { $_.Name -ne "show_transcript.py" } |
            ForEach-Object { Copy-Item $_.FullName "$staging\pipeline\" }
    }

    # ---- 5. Whisper model (Full only) ----
    if ($Variant -eq "full") {
        Invoke-Step "Bundle Whisper turbo model" {
            $snapshot = "$env:USERPROFILE\.cache\huggingface\hub\models--$($ModelRepo -replace '/', '--')\snapshots\$ModelRev"
            if (-not (Test-Path $snapshot)) {
                throw "Model cache snapshot not found: $snapshot"
            }
            New-Item -ItemType Directory -Path "$staging\models\turbo" -Force | Out-Null
            Copy-Item "$snapshot\*" "$staging\models\turbo\" -Recurse
        }
    }

    # ---- 6. Mode marker + manifests ----
    Set-Content "$staging\.portable-mode" $Variant -Encoding ASCII
    Invoke-Step "Write manifests" {
        @"
Transcriber: $Version
.NET: 10 self-contained (win-x64)
Python: $PythonVer (embeddable)
faster-whisper: 1.2.1
CTranslate2: 4.8.1
PyAV: 18.1.0
Model: turbo ($ModelRepo @ $ModelRev)
Model mode: $Variant
Architecture: win-x64
Build date: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz")
"@ | Set-Content "$staging\VERSION.txt" -Encoding UTF8

        $notices = @"
Transcriber - Third-Party Notices

This distribution bundles the following third-party components.
Full license texts are shipped in the licenses\ folder and inside each
Python package's .dist-info directory under python\Lib\site-packages.

Component                       Version       License
------------------------------- ------------- -------------------------
.NET Runtime (self-contained)   10.x          MIT (https://github.com/dotnet/runtime)
MaterialDesignThemes            5.3.2         MIT
MaterialDesignColors            5.3.2         MIT
Python (embeddable)             $PythonVer   PSF License v2
faster-whisper                  1.2.1         MIT
CTranslate2                     4.8.1         MIT
PyAV (av)                       18.1.0        BSD-3-Clause
numpy                           2.5.2         BSD-3-Clause
onnxruntime                     1.28.0        MIT
tokenizers                      0.23.1        Apache-2.0
huggingface-hub                 1.27.0        Apache-2.0
protobuf                        7.35.1        BSD-3-Clause
PyYAML                          6.0.3         MIT
httpx                           0.28.1        BSD-3-Clause
h11                             0.16.0        MIT
httpcore                        1.0.9         BSD-3-Clause
anyio                           4.14.2        MIT
certifi                         2026.7.22     MPL-2.0
idna                            3.18          BSD-3-Clause
click                           8.4.2         BSD-3-Clause
colorama                        0.4.6         BSD-3-Clause
filelock                        3.32.3        Unlicense
flatbuffers                     25.12.19      Apache-2.0
fsspec                          2026.7.0      BSD-3-Clause
hf-xet                          1.6.0         MIT
packaging                       26.3          Apache-2.0 OR BSD-2-Clause
setuptools                      84.0.0        MIT
tqdm                            4.70.0        MPL-2.0
typing_extensions               4.16.0        PSF-2.0

Whisper model: faster-whisper-large-v3-turbo (Systran/mobiuslabsgmbh conversion,
MIT, derived from OpenAI Whisper MIT). Model files are redistributed as-is.

The Windows audio capture implementation uses native Win32/WASAPI APIs
(Microsoft Windows SDK; subject to Microsoft's license terms).

Python license text (PSF): https://docs.python.org/3/license.html
"@
        Set-Content "$staging\THIRD-PARTY-NOTICES.txt" $notices -Encoding UTF8

        @"
Transcriber

Requirements:
- Windows 11 x64
- normal user account
- microphone
- supported call application

Run:
Transcriber.Desktop.exe

Validated:
- Telegram Desktop
- Google Meet via Firefox

Known limitation:
- Bluetooth HFP may be unreliable

Data:
- Calls: Documents\Transcriber\Calls
- Settings/logs: LocalAppData\Transcriber
"@ | Set-Content "$staging\README.txt" -Encoding UTF8

        Get-WheelLicenseFiles "$staging\python\Lib\site-packages" "$staging\licenses"
    }

    # ---- 7. ZIP + SHA-256 ----
    Invoke-Step "Create ZIP" {
        $zipName = "Transcriber-win-x64-full.zip"
        $zipPath = "$Artifacts\$zipName"
        if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
        Set-Content "$zipPath.sha256" $hash -Encoding ASCII
        Write-Host "ZIP: $zipPath ($([math]::Round((Get-Item $zipPath).Length/1MB,1)) MB)"
        Write-Host "SHA-256: $hash"
    }
}

New-CleanDir $Artifacts
Build-Variant "full"
Write-Host ""
Write-Host "Done. Artifacts in $Artifacts" -ForegroundColor Green
