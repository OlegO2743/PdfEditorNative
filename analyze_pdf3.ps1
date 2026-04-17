function Read-Bytes {
    param([byte[]]$data, [int]$offset, [int]$len)
    $end = [Math]::Min($offset + $len, $data.Length)
    $bytes = $data[$offset..($end-1)]
    -join ($bytes | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { '.' } })
}

# --- poradnik2013 (50MB, linearized) ---
Write-Host "=== poradnik2013.pdf ==="
$data = [System.IO.File]::ReadAllBytes("D:\Projects\C#\PdfEditorNative_31\PdfEditorNative\poradnik2013.pdf")
Write-Host "At offset 0: $(Read-Bytes $data 0 80)"
Write-Host "At offset 116: $(Read-Bytes $data 116 200)"

# Find /Prev in tail xref
$tail = Read-Bytes $data ($data.Length - 500) 500
Write-Host "Last 500 bytes: $tail"

# Find /Prev value in the xref at 116
$xrefArea = Read-Bytes $data 116 2000
Write-Host "xref area (116..2116): $xrefArea"

# --- REVIT ---
Write-Host "`n=== REVIT 2027 OPTI.pdf ==="
$data2 = [System.IO.File]::ReadAllBytes("D:\Projects\C#\PdfEditorNative_31\PdfEditorNative\REVIT 2027 OPTI.pdf")
$tail2 = Read-Bytes $data2 ($data2.Length - 1000) 1000
Write-Host "Last 1000 bytes: $tail2"
# Find startxref area
$sxOff = 1958893
Write-Host "At startxref offset $sxOff (100 bytes): $(Read-Bytes $data2 $sxOff 100)"

# --- 3rd PDF ---
Write-Host "`n=== 20260409...pdf ==="
$data3 = [System.IO.File]::ReadAllBytes("D:\Projects\C#\PdfEditorNative_31\PdfEditorNative\20260409164221915_de640cea-c449-4551-bd85-4b337f334b59.pdf")
$tail3 = Read-Bytes $data3 ($data3.Length - 500) 500
Write-Host "Last 500 bytes: $tail3"
$sxOff3 = 3271017
Write-Host "At xref offset $sxOff3 (150 bytes): $(Read-Bytes $data3 $sxOff3 150)"
