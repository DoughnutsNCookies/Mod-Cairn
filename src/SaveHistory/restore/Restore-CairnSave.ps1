# Restore a Cairn save from the copies SaveHistory made.
# Double-click "Restore Cairn Save.cmd" next to this file. Pick a copy in the list and click OK.
# Two choices after picking:
#   Replace - put the save back into its own slot. Your current save is copied first
#             ("before restore"), so this can always be undone.
#   Add     - add the save to the game's menu as a new slot (A-L). Nothing is replaced.
param(
    [string]$Pick,                          # optional: a snapshot file name, to skip the picker window
    [ValidateSet('', 'Replace', 'Add')][string]$Mode = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem, System.Windows.Forms
$history  = $PSScriptRoot
$saveRoot = Join-Path (Split-Path $history -Parent) 'Cairn_RETAIL'

function Show-Box($text, $buttons = 'OK', $icon = 'Information') {
    [System.Windows.Forms.MessageBox]::Show($text, 'Restore Cairn save', $buttons, $icon)
}

function Read-Note($zipPath) {
    $info = @{ reason = ''; files = @() }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $note = $zip.GetEntry('_snapshot.txt')
        if ($note) {
            $r = New-Object System.IO.StreamReader($note.Open())
            try {
                while ($null -ne ($line = $r.ReadLine())) {
                    if ($line -like 'reason=*') { $info.reason = $line.Substring(7) }
                }
            } finally { $r.Dispose() }
        }
        $info.files = @($zip.Entries | Where-Object { $_.FullName -match '(^|/)SLOT_[^/]*\.CPK$' } | ForEach-Object FullName)
    } finally { $zip.Dispose() }
    $info
}

if (Get-Process -Name Cairn -ErrorAction SilentlyContinue) {
    Show-Box "Cairn is still running. Close the game first, then run this again." 'OK' 'Warning' | Out-Null
    exit 1
}
if (-not (Test-Path $saveRoot)) {
    Show-Box "Can't find the save folder:`n$saveRoot" 'OK' 'Error' | Out-Null
    exit 1
}

$rows = foreach ($z in Get-ChildItem $history -Filter *.zip | Sort-Object LastWriteTime -Descending) {
    $n = Read-Note $z.FullName
    if ($n.files.Count -eq 0) { continue }   # settings-only copies are not offered
    [pscustomobject]@{
        When    = $z.LastWriteTime.ToString('yyyy-MM-dd  HH:mm:ss')
        Slot    = ($n.files | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }) -join ' + '
        Reason  = $n.reason
        'Size (MB)' = [math]::Round($z.Length / 1MB, 1)
        File    = $z.Name
    }
}
if (-not $rows) {
    Show-Box "No save copies yet in:`n$history`n`nPlay with the SaveHistory mod installed and they will appear here." | Out-Null
    exit 0
}

if ($Pick) { $choice = $rows | Where-Object File -eq $Pick | Select-Object -First 1 }
else { $choice = $rows | Out-GridView -Title 'Pick the save to go back to (newest first), then click OK' -OutputMode Single }
if (-not $choice) { exit 0 }

if (-not $Mode) {
    $ans = Show-Box ("$($choice.Slot)   $($choice.When)`n`n" +
        "Yes = add it to the game menu as a new slot (nothing is replaced)`n" +
        "No = put it back into slot $($choice.Slot), replacing what is there now (your current save is copied first)`n" +
        "Cancel = do nothing") 'YesNoCancel' 'Question'
    if ($ans -eq 'Yes') { $Mode = 'Add' } elseif ($ans -eq 'No') { $Mode = 'Replace' } else { exit 0 }
}

$slotPattern = '(^|/)SLOT_[^/]*\.CPK$'

if ($Mode -eq 'Add') {
    $story = Join-Path $saveRoot 'SAVEGAMES\RETAIL\STORY'
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $history $choice.File))
    $added = @()
    try {
        foreach ($e in $zip.Entries | Where-Object { $_.FullName -match $slotPattern }) {
            $free = 'ABCDEFGHIJKL'.ToCharArray() | Where-Object { -not (Test-Path (Join-Path $story "SLOT_$_.CPK")) } | Select-Object -First 1
            if (-not $free) {
                if (-not $Pick) { Show-Box "All 12 slots in the game menu are in use. Erase one in the game first, then try again." 'OK' 'Warning' | Out-Null }
                Write-Output "Menu full"; exit 1
            }
            $dest = Join-Path $story "SLOT_$free.CPK"
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, "$dest.restoring", $false)
            Move-Item -LiteralPath "$dest.restoring" -Destination $dest
            $added += "slot $free"
        }
    } finally { $zip.Dispose() }
    if (-not $Pick) { Show-Box ("Done. The save from $($choice.When) is now in the game menu as $($added -join ', ').") | Out-Null }
    Write-Output "Added $($choice.File) to the menu as $($added -join ', ')"
    exit 0
}

# 1. Safety copy of everything on disk now.
$stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$slots = @(Get-ChildItem $saveRoot -Recurse -File | Where-Object Name -like 'SLOT_*.CPK')
$label = if ($slots.Count -gt 3) { "$($slots.Count) slots" } else { ($slots | ForEach-Object BaseName) -join '+' }
$safety = Join-Path $history "$stamp $label before restore.zip"
$zip = [System.IO.Compression.ZipFile]::Open($safety, 'Create')
try {
    $lines = @('reason=before restore', "time=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    foreach ($f in Get-ChildItem $saveRoot -Recurse -File) {
        $rel = $f.FullName.Substring($saveRoot.Length + 1)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel.Replace('\', '/'), 'Optimal') | Out-Null
        $lines += "file=$rel sha256=$((Get-FileHash $f.FullName -Algorithm SHA256).Hash)"
    }
    $w = New-Object System.IO.StreamWriter($zip.CreateEntry('_snapshot.txt').Open())
    try { $lines | ForEach-Object { $w.WriteLine($_) } } finally { $w.Dispose() }
} finally { $zip.Dispose() }

# 2. Put the chosen slot file(s) back.
$zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $history $choice.File))
try {
    foreach ($e in $zip.Entries | Where-Object { $_.FullName -match $slotPattern }) {
        $dest = Join-Path $saveRoot ($e.FullName.Replace('/', '\'))
        $tmp = "$dest.restoring"
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $tmp, $true)
        Move-Item -LiteralPath $tmp -Destination $dest -Force
    }
} finally { $zip.Dispose() }

if (-not $Pick) {
    Show-Box ("Done. $($choice.Slot) is back to $($choice.When).`n`n" +
        "Your previous save is in the list as `"before restore`" ($stamp).") | Out-Null
}
Write-Output "Restored $($choice.Slot) from $($choice.File); safety copy $([IO.Path]::GetFileName($safety))"
