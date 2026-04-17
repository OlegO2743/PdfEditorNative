function Analyze-PDF {
    param([string]$path)
    Write-Host "`n=== $path ==="
    $data = [System.IO.File]::ReadAllBytes($path)
    Write-Host "Size: $($data.Length) bytes"
    $hdr = [System.Text.Encoding]::Latin1.GetString($data, 0, [Math]::Min(16, $data.Length))
    Write-Host "Header: $hdr"

    $needle = [System.Text.Encoding]::Latin1.GetBytes("startxref")
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
        Write-Host "startxref found at offset $found ($($data.Length - $found) bytes from end)"
        # Read the value after startxref
        $valStr = [System.Text.Encoding]::Latin1.GetString($data, $found+9, 20).Trim()
        Write-Host "startxref offset value: $($valStr.Split()[0])"
    } else {
        Write-Host "startxref NOT in last 8192 bytes - trying full scan..."
        for ($i = 0; $i -le $data.Length - $needle.Length; $i++) {
            $ok = $true
            for ($j = 0; $j -lt $needle.Length; $j++) {
                if ($data[$i+$j] -ne $needle[$j]) { $ok = $false; break }
            }
            if ($ok) { $found = $i; break }
        }
        if ($found -ge 0) { Write-Host "startxref found at offset $found in full scan" }
        else { Write-Host "startxref NEVER found!" }
    }

    $tail = [System.Text.Encoding]::Latin1.GetString($data, [Math]::Max(0, $data.Length-8192), [Math]::Min(8192, $data.Length))
    if ($tail.Contains("/Encrypt")) { Write-Host "WARNING: ENCRYPTED PDF" }

    $head = [System.Text.Encoding]::Latin1.GetString($data, 0, [Math]::Min(1024, $data.Length))
    if ($head.Contains("/Linearized")) { Write-Host "LINEARIZED PDF" }

    # Check content of first page for Forms/XObjects
    if ($tail.Contains("/Form")) { Write-Host "Contains Form XObjects" }
    if ($tail.Contains("/XObject")) { Write-Host "Contains XObjects" }
    if ($tail.Contains("xref")) { Write-Host "Classic xref in tail" }
    if ($tail.Contains("stream")) { Write-Host "Xref streams used" }
}

Analyze-PDF "D:\Projects\C#\PdfEditorNative_31\PdfEditorNative\poradnik2013.pdf"
Analyze-PDF "D:\Projects\C#\PdfEditorNative_31\PdfEditorNative\REVIT 2027 OPTI.pdf"
Analyze-PDF "D:\Projects\C#\PdfEditorNative_31\PdfEditorNative\20260409164221915_de640cea-c449-4551-bd85-4b337f334b59.pdf"
