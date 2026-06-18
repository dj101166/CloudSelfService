# save-plan-to-project.ps1
#
# PostToolUse hook (matcher: Write|Edit|ExitPlanMode).
#
# Plan mode authors its plan into the GLOBAL scratch dir (~/.claude/plans/<random-name>.md)
# via the Write tool, and that file is never committed beside the code it describes. This
# hook detects when the just-written file lives in that global plans dir and copies it into
# THIS project's .claude/plans/ folder, named from the plan's first "# " heading. It is a
# no-op for every other tool call, so the plan ends up committed automatically with zero
# manual steps.
#
# Input: PostToolUse hook JSON on stdin (tool_name, tool_input.file_path, cwd, ...).
# Env:   CLAUDE_PROJECT_DIR is set by Claude Code to the project root.
# Exit:  always 0 (PostToolUse cannot block; this hook only ever copies a file).

$ErrorActionPreference = 'SilentlyContinue'

try { $raw = [Console]::In.ReadToEnd() } catch { exit 0 }
if (-not $raw) { exit 0 }
try { $j = $raw | ConvertFrom-Json } catch { exit 0 }

$globalPlans = Join-Path $env:USERPROFILE '.claude\plans'
if (-not (Test-Path -LiteralPath $globalPlans)) { exit 0 }
$globalPlansFull = (Resolve-Path -LiteralPath $globalPlans).Path.TrimEnd('\')

# Identify the plan file just written.
$src = $null
$f = $j.tool_input.file_path
if ($f -and (Test-Path -LiteralPath $f)) {
    $resolved = (Resolve-Path -LiteralPath $f).Path
    $parent = (Split-Path -Parent $resolved).TrimEnd('\')
    if (($parent -ieq $globalPlansFull) -and ([IO.Path]::GetExtension($resolved) -ieq '.md')) {
        $src = $resolved
    }
}
# Fallback: ExitPlanMode carries no file path, so take the newest plan in the global dir.
if (-not $src -and $j.tool_name -eq 'ExitPlanMode') {
    $newest = Get-ChildItem -LiteralPath $globalPlansFull -Filter '*.md' -File |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest) { $src = $newest.FullName }
}
if (-not $src) { exit 0 }

$proj = $env:CLAUDE_PROJECT_DIR
if (-not $proj) { $proj = $j.cwd }
if (-not $proj -or -not (Test-Path -LiteralPath $proj)) { exit 0 }

$destDir = Join-Path $proj '.claude\plans'
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

# Name the copy from the plan's first H1 heading; fall back to the source file name.
$content = Get-Content -LiteralPath $src -Raw
$slug = $null
$m = [regex]::Match($content, '(?m)^#\s+(.+?)\s*$')
if ($m.Success) {
    $slug = $m.Groups[1].Value.ToLower()
    $slug = ($slug -replace '[^a-z0-9]+', '-').Trim('-')
    if ($slug.Length -gt 80) { $slug = $slug.Substring(0, 80).Trim('-') }
}
if (-not $slug) { $slug = [IO.Path]::GetFileNameWithoutExtension($src) }

$dest = Join-Path $destDir ($slug + '.md')
Copy-Item -LiteralPath $src -Destination $dest -Force
exit 0
