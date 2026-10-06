$ErrorActionPreference = 'Stop'
$repo  = 'C:\Users\user\win-caption-menu'
$utf8  = New-Object System.Text.UTF8Encoding($false)

# ---------------------------------------------------------------------------
# Decorative characters, by Unicode block:
#   U+2100..U+213F  symbols: ℹ ℻ ™ № ℓ ℮
#   U+2190..U+21FF  arrows
#   U+2300..U+23FF  technical / media controls
#   U+2460..U+24FF  circled digits
#   U+25A0..U+27BF  geometric shapes, dingbats, check and cross marks
#   U+2900..U+297F  supplementary arrows
#   U+2B00..U+2BFF  stars, block arrows
#   U+FE0E/U+FE0F   variation selectors that force emoji presentation
#   U+200D          zero-width joiner (glues compound emoji)
#   U+20E3          combining enclosing keycap
#   surrogate pairs anything outside the BMP: emoji, flags, pictographs
#
# Typography we deliberately KEEP: em dash, en dash, ellipsis, guillemets and
# box drawing. Those are readable punctuation, not decoration.
# ---------------------------------------------------------------------------
$emoji = '[\uD800-\uDBFF][\uDC00-\uDFFF]' +
         '|[\u2100-\u213F\u2190-\u21FF\u2300-\u23FF\u2460-\u24FF\u25A0-\u27BF\u2900-\u297F\u2B00-\u2BFF]' +
         '|[\uFE0E\uFE0F]|\u200D|\u20E3'

# Status markers first: the test prints these, so they need ASCII stand-ins.
$markers = @{
    '[\u25B6\uFE0F]' = '[..]'   # step
    '[\u2705]'       = '[ok]'   # pass
    '[\u274C]'       = '[!!]'   # fail
    '[\u2139\uFE0F]' = '[--]'   # note
    '[\u27A1]'       = '>'      # arrow
    '[\uD83D\uDD34]' = 'TEST'   # red circle
    '[\uD83D\uDCC2]' = 'TEST'   # green circle
}

$files = @(
    (Join-Path $repo 'Test-CaptionMenu.ps1'),
    (Join-Path $repo 'tools\Strip-Decoration.ps1'),
    (Join-Path $repo 'README.md'),
    (Join-Path $repo 'docs\RELEASING.md'),
    (Join-Path $repo 'assets\ICON-PROMPT.md'),
    (Join-Path $repo '.gitignore')
) + (Get-ChildItem (Join-Path $repo 'src\CaptionMenu') -File | Select-Object -ExpandProperty FullName)

foreach ($f in $files) {
    if (-not (Test-Path $f)) { continue }
    $t = [IO.File]::ReadAllText($f)
    $before = ([regex]::Matches($t, $emoji)).Count
    if ($before -eq 0) { continue }

    foreach ($k in $markers.Keys) { $t = [regex]::Replace($t, $k, $markers[$k]) }
    $t = [regex]::Replace($t, '[\u2190-\u21FF\u2900-\u297F]\uFE0F?', '->')  # arrows -> ->
    $t = [regex]::Replace($t, $emoji, '')

    # Tidy comment spacing without ever touching indentation.
    $lines = $t -split "`r?`n"
    $out = foreach ($l in $lines) {
        if ($l -match '^(\s*)(//|#)(.*)$') {
            $pre = $Matches[1]; $mk = $Matches[2]; $body = $Matches[3]
            $body = $body -replace ' {2,}', ' '
            if ($body.Trim() -eq '') { "$pre$mk" } else { "$pre$mk $body" }
        } else { $l }
    }

    [IO.File]::WriteAllText($f, (($out -join "`n")), $utf8)
    $after = ([regex]::Matches([IO.File]::ReadAllText($f), $emoji)).Count
    Write-Host ('{0,-46} {1,4} -> {2}' -f (Split-Path -Leaf $f), $before, $after)
}
Write-Host 'done'
