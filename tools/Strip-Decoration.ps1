# ═══════════════════════════════════════════════════════════════════════════
# Strip-Decoration.ps1 — removes emoji and decorative Unicode from source files.
# ═══════════════════════════════════════════════════════════════════════════
#
# WHY: this repository is read as an engineering artifact. Emoji in comments,
# docs and tool output read as decoration rather than documentation, so we keep
# the prose and drop the decoration.
#
# WHAT COUNTS AS DECORATION:
#   * Everything outside the BMP (U+1F000..U+1FAFF): emoji, flags, ZWJ sequences.
#   * BMP symbols: emoji-presentation arrows, stars, check marks, crosses,
#     warning signs, technical symbols (U+2190..U+21FF, U+2300..U+23FF,
#     U+2460..U+24FF, U+25A0..U+27BF, U+2900..U+297F, U+2B00..U+2BFF).
#   * Modifiers that only exist to make the above render as emoji:
#     variation selectors (U+FE0E/U+FE0F), ZWJ (U+200D), keycap (U+20E3).
#
# WHAT IS KEPT:
#   * Code, identifiers, string literals and behaviour: untouched.
#   * Box drawing used as a visual rule inside comments is converted to "---".
#   * LICENSE: never touched, it is a legal text.
#
# IMPORTANT IMPLEMENTATION NOTE — an earlier version of this script collapsed
# every run of three or more spaces into two, which silently destroyed code
# indentation (4-space blocks became 2). The script below therefore works line
# by line and only ever touches whitespace directly after a comment marker, so
# indentation can never be affected.
#
# USAGE:  pwsh -File .\tools\Strip-Decoration.ps1 [-WhatIf]

param([switch]$WhatIf) # -WhatIf reports what would change without writing anything

$ErrorActionPreference = 'Stop'   # An error here is an error, not a silent skip
Set-StrictMode -Version Latest    # Property access on missing members should fail loudly

$repoRoot = Split-Path -Parent $PSScriptRoot # Repository root (this script lives in tools/)

# Emoji and symbol characters, as one alternation. Surrogate pairs come first so
# that astral characters are consumed whole rather than leaving orphan halves.
$emojiPattern = @(
    '[\uD800-\uDBFF][\uDC00-\uDFFF]'  # astral plane: emoji, flags, ZWJ sequences
    '|[\u2190-\u21FF]'                # arrows (incl. emoji presentation forms)
    '|[\u2300-\u23FF]'                # technical symbols, hourglass, alarm clock
    '|[\u2460-\u24FF]'                # circled digits
    '|[\u25A0-\u27BF]'                # geometric shapes, dingbats, check/cross marks
    '|[\u2900-\u297F]'                # supplementary arrows
    '|[\u2B00-\u2BFF]'                # stars, up/down block arrows
    '|[\uFE0E\uFE0F]'                 # variation selectors 15/16
    '|\u200D'                         # zero width joiner
    '|\u20E3'                         # combining enclosing keycap
) -join ''

$emojiRx     = [regex]::new($emojiPattern)                      # main pattern
$boxRuleRx   = [regex]::new('[\u2500-\u257F]{2,}')               # box-drawing rules in comments
$trailingRx  = [regex]::new('[ \t]+$', [System.Text.RegularExpressions.RegexOptions]::Multiline)
$leadingSpRx = [regex]::new('\A +')                              # indentation, captured for restore

# Comment markers are located inside Remove-Decoration, not here: a line may carry a
# trailing comment after code (for example "var x = 1; // note"), so a marker must be
# searched anywhere on the line rather than only at its start.

function Remove-Decoration {
    <#
    .SYNOPSIS
        Strips decoration from a single line while preserving code and indentation exactly.

    .DESCRIPTION
        A line is split into a code part and a comment part at the first comment marker.
        Only the comment part is cleaned: decoration removed, box rules folded to "---",
        and whitespace after the marker normalised to a single space. The code part and
        the leading whitespace are written back verbatim.

        Strings are respected: if a "//" appears inside a string literal (an odd number
        of quotes before it), the line is treated as having no comment at all.
    #>
    param([string]$Line)

    if ($Line.Trim() -eq '') { return $Line }  # Blank line: nothing to do

    # Locate the comment marker. Markers are tried longest-first so that "//" wins
    # over "#" when both could match at the same position.
    $index = -1
    $marker = ''

    foreach ($candidate in @('//', '#', '--')) {
        $at = $Line.IndexOf($candidate, [System.StringComparison]::Ordinal)
        if ($at -lt 0) { continue }

        # A marker inside a string literal is not a comment. Count quotes before it:
        # an odd count means we are inside a string, so this occurrence must be ignored
        # and we look for the next one further right.
        $quotesBefore = ([regex]::Matches($Line.Substring(0, $at), '"')).Count
        if ($quotesBefore % 2 -eq 1) {
            $at = $Line.IndexOf($candidate, $at + $candidate.Length, [System.StringComparison]::Ordinal)
            if ($at -lt 0) { continue }
            $quotesBefore = ([regex]::Matches($Line.Substring(0, $at), '"')).Count
            if ($quotesBefore % 2 -eq 1) { continue }
        }

        if ($index -lt 0 -or $at -lt $index) { $index = $at; $marker = $candidate }
    }

    # No comment on this line: pure code, returned untouched.
    if ($index -lt 0) { return $Line }

    # A documentation line such as "/// <summary>" or "/** ... */" uses extra slashes.
    # Consume them all, otherwise removing the emoji from a doc line leaves a stray
    # "/ <summary>" which is not valid documentation and reads as corruption.
    $slashRun = 0
    while ($index + $slashRun -lt $Line.Length -and $Line[$index + $slashRun] -eq '/') {
        $slashRun++
    }

    $codePart = $Line.Substring(0, $index)             # Code and indentation, kept verbatim
    $body = $Line.Substring($index + $slashRun)         # Comment text after all slashes

    $prefix = $Line.Substring($index, $slashRun)

    # A line that is only a marker has no body to preserve.
    if ($body.Trim() -eq '') { return $codePart + $prefix }

    $body = $emojiRx.Replace($body, '')        # Drop decoration
    $body = $boxRuleRx.Replace($body, '---')   # Box rules become plain dashes
    $body = $body.Trim()                       # Trim both ends before re-padding
    $body = [regex]::Replace($body, ' {2,}', ' ') # Collapse leftovers

    # Re-emit the original slash run so that "///" stays "///" and "/**" stays "/**".
    $prefix = $Line.Substring($index, $slashRun)
    return $codePart + $prefix + ' ' + $body
}

function Get-TrackedFiles {
    <#
    .SYNOPSIS
        Lists files under git control in the repository root.

    .NOTES
        Only tracked files are touched: build output under bin/ and obj/ must never
        be rewritten, and an untracked scratch file should not be reformatted by accident.
    #>
    param([string]$Root)

    $out = & git -C $Root ls-files 2>$null
    if (-not $out) { throw 'git ls-files returned nothing — is this a git repository?' }
    return $out
}

# Extensions we are willing to rewrite, plus dotfiles such as .gitignore which have no
# real extension: [IO.Path]::GetExtension('.gitignore') returns '.gitignore', not '', so
# they must be matched by name.
$textExtensions = @('.md', '.yml', '.yaml', '.cs', '.csproj', '.ps1')
$dotfileNames   = @('.gitignore')

# Fence state for Markdown: code blocks are left alone so that sample C#/XML keeps its
# exact formatting. Everything else in a Markdown file is prose or a table, where the
# decoration is just noise and can go.
$inFence = $false

function Remove-DecorationFromMarkdown {
    <#
    .SYNOPSIS
        Strips decoration from one Markdown line, skipping fenced code blocks.
    #>
    param(
        [string]$Line,
        [ref]$FenceState # Toggles on ``` fences
    )

    $trimmed = $Line.TrimStart()

    # Fence delimiter: flip state and keep the line exactly as written.
    if ($trimmed.StartsWith('```')) {
        $FenceState.Value = -not $FenceState.Value
        return $Line
    }

    if ($FenceState.Value) { return $Line }  # Inside a code block: untouched

    # Prose line: decoration can be removed anywhere on it, code indentation is irrelevant
    # here because Markdown structure lives in the line's first characters, not in blanks.
    $out = $emojiRx.Replace($Line, '')   # Drop emoji and symbols
    $out = $out -replace ' {2,}', ' '     # Collapse the gaps the emoji left behind
    return $out.TrimEnd()
}

$files = Get-TrackedFiles -Root $repoRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false) # Source files: UTF-8 without BOM
$changed = 0

foreach ($rel in $files) {
    # LICENSE is a legal text and is never modified.
    if ($rel -eq 'LICENSE') { continue }

    $leaf = Split-Path -Leaf $rel
    $ext = [IO.Path]::GetExtension($rel).ToLowerInvariant()
    $isMarkdown = ($ext -eq '.md')

    if ($leaf -notin $dotfileNames -and $ext -notin $textExtensions) { continue }

    $full = Join-Path $repoRoot $rel
    $before = [IO.File]::ReadAllText($full)

    # Markdown is prose, so decoration is removed everywhere outside code fences.
    # Source files are cleaned line by line at the comment marker, which keeps
    # indentation and string literals untouched.
    if ($isMarkdown) {
        $fence = $false
        $afterLines = foreach ($line in [System.IO.File]::ReadAllLines($full)) {
            Remove-DecorationFromMarkdown -Line $line -FenceState ([ref]$fence)
        }
    }
    else {
        $afterLines = foreach ($line in [System.IO.File]::ReadAllLines($full)) {
            Remove-Decoration -Line $line
        }
    }

    # LF endings: keeps the diff free of line-ending noise on a CRLF machine.
    $after = ($afterLines -join "`n") + "`n"

    if ($after -eq $before) { continue }

    $removed = $emojiRx.Matches($before).Count
    Write-Host ('{0,-46} {1,5} symbols removed' -f $rel, $removed)
    $changed++

    if (-not $WhatIf) {
        [IO.File]::WriteAllText($full, $after, $utf8NoBom)
    }
}

Write-Host ''
if ($WhatIf) {
    Write-Host "Would change $changed files (dry run, nothing written)." -ForegroundColor Yellow
} else {
    Write-Host "Rewrote $changed files." -ForegroundColor Green
    Write-Host 'Review the diff:  git diff --stat' -ForegroundColor DarkGray
}
