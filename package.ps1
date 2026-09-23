# Regenerate the single-file launcher after editing src/.
$parts = @(
    [IO.File]::ReadAllText("$PSScriptRoot/src/poly_header.txt").TrimEnd(),
    '#<CS>',
    [IO.File]::ReadAllText("$PSScriptRoot/src/StrataCmd.cs").TrimEnd(),
    '#</CS>',
    '#<PY>',
    [IO.File]::ReadAllText("$PSScriptRoot/src/strata.py").TrimEnd(),
    '#</PY>'
)
# cmd.exe requires CRLF; the Unix launcher strips CR before extracting Python.
$content = (($parts -join "`n") + "`n") -replace "`r?`n", "`r`n"
[IO.File]::WriteAllText("$PSScriptRoot/Citrus.cmd", $content, (New-Object Text.UTF8Encoding($false)))
