# Repository Instructions

## Execution Environment

- This workspace runs on Windows. The verified repository location is `E:\dev\sleeper`; use workspace-relative paths rather than hard-coding that location.
- The agent terminal uses **PowerShell Core (`pwsh`)**, verified as **7.6.6** on 2026-09-14. It is not Bash, cmd.exe, or Windows PowerShell 5.1 (`powershell.exe`).
- Write terminal commands and `.ps1` scripts for PowerShell 7 on Windows unless a different target is explicitly requested. Do not assume WSL, Git Bash, or Unix utilities are installed.
- Use the existing PowerShell session directly. Avoid nested `pwsh -Command` or `powershell -Command` invocations, which introduce another quoting and interpolation layer. When launching a script in a separate process is necessary, prefer `pwsh -NoProfile -File ./scripts/example.ps1`.

## PowerShell Compatibility Rules

- Do not emit Bash heredocs (`<<EOF`), `export`, `source`, `$(pwd)` as a path idiom, backslash line continuations, or cmd.exe syntax such as `%VAR%`, `set VAR=value`, and `cd /d`.
- Set environment variables with `$env:NAME = 'value'`; use `$PWD` or `Get-Location` for the working directory. Use `Join-Path` and `$PSScriptRoot` for script-relative paths.
- Prefer separate commands or multiline script blocks. PowerShell 7 supports `&&` and `||`, but Windows PowerShell 5.1 does not; do not confuse this with Bash compatibility. A semicolon does not stop execution when the preceding command fails.
- Use PowerShell cmdlets for filesystem and HTTP operations, such as `Get-ChildItem`, `Get-Content`, `Select-Object`, `Invoke-RestMethod`, and `Invoke-WebRequest`. `rg` is appropriate for code searches when installed. Avoid ambiguous aliases such as `curl`, `wget`, `ls`, and `cat`.
- Use single-quoted strings for literal paths, JSON, and regex patterns; use double quotes only when interpolation is intended. Backslash does not escape a quote in PowerShell. Invoke a quoted executable path with the call operator: `& 'C:\Program Files\Example\tool.exe'`.
- Use `${name}` before a literal colon in an interpolated string, and `$($object.Property)` for interpolated property access. PowerShell variable names are case-insensitive; do not assign to automatic variables such as `$PID` or `$Host`.
- Prefer argument arrays and splatting over command strings, `Invoke-Expression`, or fragile backtick line continuations. Pass native executable arguments separately instead of assembling one command string.
- For multiline literal text, use a single-quoted here-string: put `@'` on its own opening line and `'@` at the start of its own closing line. Do not use Bash heredoc syntax.
- Parse and generate JSON with `ConvertFrom-Json` and `ConvertTo-Json`; specify a sufficient serialization `-Depth` for nested data. Use `Get-Content -Raw` to read a complete JSON document. Avoid hand-escaped JSON passed through multiple shells.
- For dynamic JSON property names, use `$object.PSObject.Properties[$key].Value`, or parse with `ConvertFrom-Json -AsHashtable` and index with `[$key]`. Wrap pipeline results in `@(...)` when later code requires a consistently shaped array.
- Use `$ErrorActionPreference = 'Stop'` in scripts that must stop on PowerShell errors. Check `$LASTEXITCODE` immediately after native commands such as `dotnet` or `git`; the preference alone does not reliably turn native nonzero exits into terminating errors.
- Specify encoding when writing text artifacts, normally `-Encoding utf8`. Do not assume Windows PowerShell 5.1 has the same encoding defaults or cmdlet parameters as PowerShell 7.

## Validation

- Before running a newly written or changed `.ps1` script, check syntax with the installed PowerShell parser. Then run the smallest safe behavior check; parsing alone does not validate runtime behavior or native argument quoting.

```powershell
$scriptPath = './scripts/example.ps1'
$parseTokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PWD $scriptPath), [ref]$parseTokens, [ref]$parseErrors
) | Out-Null
if ($parseErrors.Count -gt 0) {
    throw ($parseErrors.Message -join [Environment]::NewLine)
}
```

- For repository commands, follow [README.md](../README.md) and [the reporting CLI documentation](../docs/reporting-cli.md). Existing host lifecycle scripts are [start-host.ps1](../scripts/start-host.ps1) and [stop-host.ps1](../scripts/stop-host.ps1).