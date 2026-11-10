<#
.SYNOPSIS
    Tải các công cụ ngoài mà PDF_Pro đóng gói kèm (qpdf, Tesseract + tessdata) vào thư mục tools/.

.DESCRIPTION
    - qpdf (Windows x64, bản phát hành chính thức trên GitHub): tải tệp zip, kiểm SHA-256 theo tệp checksum
      của chính bản phát hành (hoặc theo -QpdfSha256 khi đã ghim), giải nén vào tools/qpdf (tools/qpdf/bin/qpdf.exe).
    - Tesseract 5 (Windows x64, bản dựng của UB Mannheim trên GitHub): tải bộ cài, kiểm SHA-256 (theo
      -TesseractSha256 khi đã ghim), giải nén bằng 7-Zip nếu có (không thì chạy bộ cài im lặng /S /D=...) vào
      tools/tesseract (tools/tesseract/tesseract.exe).
    - Dữ liệu nhận dạng tessdata_best và tessdata_fast (vie, eng, osd — đổi bằng -Languages) ở thẻ -TessdataRef
      cố định trên GitHub vào tools/tesseract/tessdata-best và tessdata-fast (kèm pdf.ttf của Tesseract). Mỗi tệp
      được tính SHA-256 và so với giá trị ghim trong $TessdataSha256 (chưa ghim thì in ra để ghim); tất cả được ghi
      vào tools/tesseract/SHA256SUMS.txt.
    Các binary tải về không được commit (xem .gitignore).

.PARAMETER ToolsDirectory
    Thư mục đích. Mặc định là thư mục chứa script này.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\fetch-tools.ps1
#>
[CmdletBinding()]
param(
    [string]$ToolsDirectory,

    # Phiên bản qpdf; đổi ở đây khi nâng cấp.
    [string]$QpdfVersion = '12.2.0',

    # SHA-256 đã ghim của tệp zip (để trống: lấy từ tệp checksum của bản phát hành).
    [string]$QpdfSha256 = '',

    # Phiên bản Tesseract của UB Mannheim (thẻ phát hành không có chữ 'v').
    [string]$TesseractVersion = '5.5.0.20241111',

    # SHA-256 đã ghim của bộ cài Tesseract (để trống: in ra giá trị tính được để ghim).
    [string]$TesseractSha256 = '',

    # Thẻ (hoặc commit) của kho tessdata_best / tessdata_fast.
    [string]$TessdataRef = '4.1.0',

    # Ngôn ngữ nhận dạng (osd — nhận hướng trang — luôn được tải).
    [string[]]$Languages = @('vie', 'eng'),

    # Bộ dữ liệu cần tải.
    [ValidateSet('both', 'best', 'fast')]
    [string]$Tessdata = 'both',

    # Tải lại dù đã có.
    [switch]$Force
)

# SHA-256 đã ghim của dữ liệu nhận dạng ("best/vie.traineddata" = '...'). Để trống: in ra để ghim.
$TessdataSha256 = @{
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 leaves $PSScriptRoot empty inside param() defaults.
if (-not $ToolsDirectory) {
    $ToolsDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$tools = @(
    [pscustomobject]@{
        Name    = 'qpdf'
        Folder  = 'qpdf'
        Phase   = 9
        License = 'Apache-2.0'
        Source  = 'https://github.com/qpdf/qpdf/releases'
        Purpose = 'Linearize, object streams, khôi phục xref'
    },
    [pscustomobject]@{
        Name    = 'Tesseract OCR 5 + tessdata_best (vie, eng, osd)'
        Folder  = 'tesseract'
        Phase   = 10
        License = 'Apache-2.0'
        Source  = 'https://github.com/tesseract-ocr/tesseract'
        Purpose = 'OCR tiếng Việt và tiếng Anh'
    }
)

Write-Host "PDF_Pro - công cụ ngoài"
Write-Host "Thư mục đích: $ToolsDirectory"
Write-Host ''

foreach ($tool in $tools) {
    $target = Join-Path $ToolsDirectory $tool.Folder
    $status = if (Test-Path $target) { 'đã có' } else { 'chưa có' }
    Write-Host ("- {0} [{1}]" -f $tool.Name, $status)
    Write-Host ("    Dùng cho : {0}" -f $tool.Purpose)
    Write-Host ("    License  : {0}" -f $tool.License)
    Write-Host ("    Nguồn    : {0}" -f $tool.Source)
    Write-Host ("    Tự động tải từ Phase {0}; đích: {1}" -f $tool.Phase, $target)
}

Write-Host ''

function Install-Qpdf {
    $target = Join-Path $ToolsDirectory 'qpdf'
    $exe = Join-Path $target 'bin\qpdf.exe'
    if ((Test-Path $exe) -and -not $Force) {
        Write-Host "qpdf đã có: $exe (dùng -Force để tải lại)"
        return
    }

    $base = "https://github.com/qpdf/qpdf/releases/download/v$QpdfVersion"
    $asset = "qpdf-$QpdfVersion-msvc64.zip"
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("pdfpro-qpdf-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $zip = Join-Path $work $asset
        Write-Host "Tải $base/$asset"
        Invoke-WebRequest -Uri "$base/$asset" -OutFile $zip -UseBasicParsing

        $expected = $QpdfSha256
        if (-not $expected) {
            $sums = Join-Path $work 'qpdf.sha256'
            Write-Host "Tải tệp checksum $base/qpdf-$QpdfVersion.sha256"
            Invoke-WebRequest -Uri "$base/qpdf-$QpdfVersion.sha256" -OutFile $sums -UseBasicParsing
            $line = Get-Content $sums | Where-Object { $_ -match ('\s\*?' + [regex]::Escape($asset) + '$') } | Select-Object -First 1
            if (-not $line) { throw "Tệp checksum không có dòng cho $asset." }
            $expected = ($line -split '\s+')[0]
        }

        $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash
        if ($actual -ne $expected.ToUpperInvariant()) {
            throw "SHA-256 không khớp cho ${asset}: tính được $actual, mong đợi $expected. Không cài."
        }

        Write-Host "SHA-256 khớp: $actual"
        $unpacked = Join-Path $work 'unpacked'
        Expand-Archive -Path $zip -DestinationPath $unpacked
        $root = Get-ChildItem $unpacked -Directory | Select-Object -First 1
        if (-not $root -or -not (Test-Path (Join-Path $root.FullName 'bin\qpdf.exe'))) {
            throw "Tệp zip không có bin\qpdf.exe như mong đợi."
        }

        if (Test-Path $target) { Remove-Item -Recurse -Force $target }
        Move-Item $root.FullName $target
        & $exe --version | Select-Object -First 1 | Write-Host
        Write-Host "Đã cài qpdf vào $target"
    }
    finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

$script:Sums = [System.Collections.Generic.List[string]]::new()

# Tính SHA-256 của một tệp tải về và so với giá trị ghim (nếu có); ghi vào danh sách SHA256SUMS.
function Test-Download([string]$Path, [string]$Name, [string]$Expected) {
    $actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash
    if ($Expected) {
        if ($actual -ne $Expected.ToUpperInvariant()) {
            throw "SHA-256 không khớp cho ${Name}: tính được $actual, mong đợi $Expected. Không cài."
        }

        Write-Host "  SHA-256 khớp: $actual"
    }
    else {
        Write-Warning "  Chưa ghim SHA-256 cho $Name — giá trị tính được: $actual (ghim vào script để những lần sau kiểm tra)."
    }

    $script:Sums.Add("$actual  $Name")
}

function Find-SevenZip {
    $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($root in @($env:ProgramFiles, $env:ProgramW6432, ${env:ProgramFiles(x86)})) {
        if ($root -and (Test-Path (Join-Path $root '7-Zip\7z.exe'))) { return (Join-Path $root '7-Zip\7z.exe') }
    }

    return $null
}

function Install-Tesseract {
    $target = Join-Path $ToolsDirectory 'tesseract'
    $exe = Join-Path $target 'tesseract.exe'
    if ((Test-Path $exe) -and -not $Force) {
        Write-Host "Tesseract đã có: $exe (dùng -Force để tải lại)"
        return
    }

    $asset = "tesseract-ocr-w64-setup-$TesseractVersion.exe"
    $url = "https://github.com/UB-Mannheim/tesseract/releases/download/v$TesseractVersion/$asset"
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("pdfpro-tesseract-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $setup = Join-Path $work $asset
        Write-Host "Tải $url"
        Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing
        Test-Download $setup $asset $TesseractSha256

        $staging = Join-Path $work 'tesseract'
        $sevenZip = Find-SevenZip
        if ($sevenZip) {
            Write-Host "Giải nén bằng $sevenZip"
            & $sevenZip x -y "-o$staging" $setup | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7-Zip không giải nén được $asset (mã $LASTEXITCODE)." }
            Get-ChildItem $staging -Force | Where-Object { $_.Name -like '$*' -or $_.Name -like 'uninstall*' } |
                Remove-Item -Recurse -Force
        }
        else {
            # NSIS: /D phải đứng cuối và không có ngoặc kép. Có thể hiện hộp xác nhận quyền quản trị.
            Write-Host 'Không thấy 7-Zip: chạy bộ cài im lặng vào thư mục tạm.'
            $process = Start-Process -FilePath $setup -ArgumentList @('/S', "/D=$staging") -Wait -PassThru
            if ($process.ExitCode -ne 0) { throw "Bộ cài Tesseract kết thúc với mã $($process.ExitCode)." }
        }

        if (-not (Test-Path (Join-Path $staging 'tesseract.exe'))) {
            throw "Bộ cài không có tesseract.exe như mong đợi."
        }

        if (Test-Path $target) { Remove-Item -Recurse -Force $target }
        Move-Item $staging $target
        & $exe --version 2>&1 | Select-Object -First 1 | Write-Host
        Write-Host "Đã cài Tesseract vào $target"
    }
    finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

function Install-Tessdata([string]$Kind) {
    $tesseract = Join-Path $ToolsDirectory 'tesseract'
    $folder = Join-Path $tesseract "tessdata-$Kind"
    New-Item -ItemType Directory -Path $folder -Force | Out-Null

    # Tesseract ghi PDF lớp chữ bằng phông không nét pdf.ttf, tìm trong thư mục dữ liệu được chỉ định.
    $font = Join-Path $tesseract 'tessdata\pdf.ttf'
    if (-not (Test-Path $font)) { throw "Không thấy $font trong bản Tesseract đã cài." }
    Copy-Item $font (Join-Path $folder 'pdf.ttf') -Force

    foreach ($language in (@($Languages) + 'osd' | Select-Object -Unique)) {
        if ($language -notmatch '^[A-Za-z0-9_]+$') { throw "Mã ngôn ngữ không hợp lệ: $language" }
        $file = Join-Path $folder "$language.traineddata"
        $name = "$Kind/$language.traineddata"
        if ((Test-Path $file) -and -not $Force) {
            Write-Host "  $name đã có"
            $script:Sums.Add("$((Get-FileHash -Algorithm SHA256 -Path $file).Hash)  $name")
            continue
        }

        $url = "https://github.com/tesseract-ocr/tessdata_$Kind/raw/$TessdataRef/$language.traineddata"
        Write-Host "Tải $url"
        $download = "$file.download"
        Invoke-WebRequest -Uri $url -OutFile $download -UseBasicParsing
        try {
            Test-Download $download $name $TessdataSha256[$name]
            Move-Item $download $file -Force
        }
        finally {
            Remove-Item $download -Force -ErrorAction SilentlyContinue
        }
    }
}

Install-Qpdf
Write-Host ''
Install-Tesseract
$kinds = if ($Tessdata -eq 'both') { @('best', 'fast') } else { @($Tessdata) }
foreach ($kind in $kinds) { Install-Tessdata $kind }
if ($script:Sums.Count -gt 0) {
    $sumsFile = Join-Path $ToolsDirectory 'tesseract\SHA256SUMS.txt'
    Set-Content -Path $sumsFile -Value $script:Sums -Encoding UTF8
    Write-Host "SHA-256 của Tesseract và dữ liệu: $sumsFile"
}

exit 0
