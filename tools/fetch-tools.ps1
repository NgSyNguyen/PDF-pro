<#
.SYNOPSIS
    Tải các công cụ ngoài mà PDF_Pro đóng gói kèm (qpdf, Tesseract + tessdata) vào thư mục tools/.

.DESCRIPTION
    - qpdf (Windows x64, bản phát hành chính thức trên GitHub): tải tệp zip, kiểm SHA-256 theo tệp checksum
      của chính bản phát hành (hoặc theo -QpdfSha256 khi đã ghim), giải nén vào tools/qpdf (tools/qpdf/bin/qpdf.exe).
    - Phase 10 bổ sung tải Tesseract 5 + tessdata_best (vie, eng, osd) và kiểm tra SHA-256.
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

    # Tải lại dù đã có.
    [switch]$Force
)

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

Install-Qpdf
Write-Host ''
Write-Host 'Tesseract (Phase 10) chưa được tải tự động.'
exit 0
