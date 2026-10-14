<#
.SYNOPSIS
    Tải các công cụ ngoài mà PDF_Pro đóng gói kèm (qpdf, Tesseract + tessdata) vào thư mục tools/.

.DESCRIPTION
    Phase 1: bản khung, chỉ kiểm tra và in hướng dẫn, chưa tải gì.
    - Phase 9 bổ sung tải qpdf (Windows x64) và kiểm tra SHA-256.
    - Phase 10 bổ sung tải Tesseract 5 + tessdata_best (vie, eng, osd) và kiểm tra SHA-256.
    Các binary tải về không được commit (xem .gitignore).

.PARAMETER ToolsDirectory
    Thư mục đích. Mặc định là thư mục chứa script này.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\fetch-tools.ps1
#>
[CmdletBinding()]
param(
    [string]$ToolsDirectory
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
Write-Host 'Phase 1 chưa cần công cụ ngoài nào. Script này chưa tải gì.'
exit 0
