function Analyze-PDF {
    param([string]$path)
    Write-Host "`n=== $([System.IO.Path]::GetFileName($path)) ==="
    $data = [System.IO.File]::ReadAllBytes($path)
    Write-Host "Size: $($data.Length) bytes"

    # Header (first 16 bytes as ASCII)
    $hdrBytes = $data[0..15]
    $hdr = -join ($hdrBytes | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { '.' } })
    Write-Host "Header: $hdr"

    # Search for startxref
    $needle = @(115,116,97,114,116,120,114,101,102)  # "startxref"
    $found = -1
    $from = [Math]::Max(0, $data.Length - 8192)
    for ($i = $from; $i -le $data.Length - $needle.Length; $i++) {
        $ok = $true
        for ($j = 0; $j -lt $needle.Length; $j++) {
            if ($data[$i+$j] -ne $needle[$j]) { $ok = $false; break }
        }
        if ($ok) { $found = $i }
    }

    if ($found -ge 0) {
        Write-Host "startxref at offset $found ($($data.Length - $found) bytes from end)"
        # Read offset value
        $pos = $found + 9
        while ($pos -lt $data.Length -and $data[$pos] -le 32) { $pos++ }
        $numStr = ""
        while ($pos -lt $data.Length -and $data[$pos] -ge 48 -and $data[$pos] -le 57) { $numStr += [char]$data[$pos]; $pos++ }
        Write-Host "startxref value: $numStr"
    } else {
        Write-Host "startxref NOT in last 8192 bytes - search full file..."
        for ($i = 0; $i -le $data.Length - $needle.Length; $i++) {
            $ok = $true
            for ($j = 0; $j -lt $needle.Length; $j++) {
                if ($data[$i+$j] -ne $needle[$j]) { $ok = $false; break }
            }
            if ($ok) { $found = $i; break }
        }
        if ($found -ge 0) { Write-Host "Found in full scan at offset $found" }
        else { Write-Host "NEVER FOUND!" }
    }

    # Check last 8KB for keywords
    $tailLen = [Math]::Min(8192, $data.Length)
    $tail = -join ($data[($data.Length - $tailLen)..($data.Length - 1)] | ForEach-Object { [char]$_ })
    if ($tail -match '/Encrypt') { Write-Host "WARNING: ENCRYPTED PDF" }
    if ($tail -match 'xref') { Write-Host "Classic xref table" }
    else { Write-Host "No classic xref in tail (uses xref streams or xref elsewhere)" }

    # Check header for linearization
    $headLen = [Math]::Min(1024, $data.Length)
    $head = -join ($data[0..($headLen-1)] | ForEach-Object { [char]$_ })
    if ($head -match '/Linearized') { Write-Host "LINEARIZED PDF" }

    # Search first occurrence of "xref" keyword
    $xrefNeedle = @(120,114,101,102)  # "xref"
    for ($i = 0; $i -le [Math]::Min(100000, $data.Length - 4); $i++) {
        if ($data[$i] -eq 120 -and $data[$i+1] -eq 114 -and $data[$i+2] -eq 101 -and $data[$i+3] -eq 102) {
            # Check if preceded by newline or start
            $prev = if ($i -gt 0) { $data[$i-1] } else { 10 }
            if ($prev -eq 10 -or $prev -eq 13 -or $i -eq 0) {
                Write-Host "First 'xref' keyword at offset $i"
                break
            }
        }
    }
}

Analyze-PDF "D:\Projects\C#\PdfEditorNative_31\PdfEditorNative\poradnik2013.pdf"
Analyze-PDF "D:\Projects\C#\PdfEditorNative_31\PdfEditorNative\REVIT 2027 OPTI.pdf"
Analyze-PDF "D:\Projects\C#\PdfEditorNative_31\PdfEditorNative\20260409164221915_de640cea-c449-4551-bd85-4b337f334b59.pdf"
