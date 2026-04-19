/**
 * ck-guards — OpenCode plugin
 *
 * Enforces the Context King code-search protocol by intercepting anti-patterns
 * before they waste tokens. In OpenCode, throw actually blocks the call.
 *
 * Deployed to: .opencode/plugin/ck-guards.ts
 * Auto-loaded by OpenCode from .opencode/plugin/ on session start.
 *
 * Guards implemented:
 *   glob on source files across a wide path   → redirect to ck find-scope
 *   grep on source files across a wide path   → redirect to ck find-scope
 *   bash cat on source files                  → redirect to ck get-method-source / Read
 *   bash grep targeting source files          → redirect to ck find-scope
 *   bash pipe on ck find-scope         → block (destroys structure)
 *
 * Hints implemented (tool.execute.after):
 *   ck find-scope with tight score cluster → suggest --min-score
 */

const CK = ".opencode/skills/ck/ck"

const SOURCE_EXT_RE = /\.(cs|tsx?)(\b|$)/

/**
 * A path is considered narrow (already scoped) when it has more than 3 segments.
 * Mirrors the shell guard logic: depth > 3 means the agent has already narrowed scope.
 * Examples:
 *   ""                              → broad (0 segments)
 *   "src"                           → broad (1 segment)
 *   "src/Modules/Inventory"         → broad (3 segments)
 *   "src/Modules/Inventory/Orders"  → narrow (4 segments) — skip guard
 */
function isNarrowPath(path: string | undefined): boolean {
  if (!path) return false
  return path.split("/").filter((s) => s.trim() !== "").length > 3
}

export default async function ckGuards() {
  return {
    "tool.execute.before": async (
      input: { tool: string },
      output: { args: Record<string, unknown> }
    ) => {
      const { tool } = input
      const args = output.args

      // ── glob: broad source file pattern ──────────────────────────────────
      if (tool === "glob") {
        const pattern = String(args.pattern ?? args.glob ?? "")
        const path = String(args.path ?? args.cwd ?? "")

        if (SOURCE_EXT_RE.test(pattern) && !isNarrowPath(path)) {
          throw new Error(
            `[ck-guard] Broad source file glob detected (pattern: "${pattern}", path: "${path || "repo root"}").

Use ck find-scope to discover the right area first:
  ${CK} find-scope --query "<multi-keyword description>"

Then explore within those folders:
  ${CK} signatures <folder>/
  ${CK} get-method-source <file> <MemberName>

Do NOT use broad glob — it wastes tokens scanning irrelevant files.`
          )
        }
        return
      }

      // ── grep: broad source file search ─────────────────────────────────────
      if (tool === "grep") {
        const globArg = String(args.glob ?? args.include ?? "")
        const typeArg = String(args.type ?? "")
        const path = String(args.path ?? args.cwd ?? "")
        const isSource =
          SOURCE_EXT_RE.test(globArg) || /^(cs|tsx?)$/.test(typeArg)

        if (isSource && !isNarrowPath(path)) {
          throw new Error(
            `[ck-guard] Broad source file grep detected (path: "${path || "repo root"}").

Use ck find-scope to discover the right area first:
  ${CK} find-scope --query "<multi-keyword description>"

Then explore within those folders:
  ${CK} signatures <folder>/
  ${CK} get-method-source <file> <MemberName>

Do NOT use broad grep — it wastes tokens scanning irrelevant files.`
          )
        }
        return
      }

      // ── bash: piped ck output or grep on source files ───────────────────
      if (tool === "bash") {
        const cmd = String(args.command ?? "")

        // Detect ck find-scope piped through head/grep/tail (real shell pipe, not regex \|)
        // Allow pipes on signatures and get-method-source (filtering large output is fine).
        if (/ck\s+find-scope\b/.test(cmd) &&
            /\|\s*(head|tail|grep|wc|sort|awk|sed|cut|less|more)\b/.test(cmd)) {
          throw new Error(
            `[ck-guard] Do NOT pipe ck output through head, grep, or tail.

ck output is already structured and scoped. Piping discards folder scores and
grouping structure you need. Instead:

  • Reduce output with --top <n> or --min-score <f>

Remove the pipe and run the ck command directly.`
          )
        }

        // Detect cat on source files (should use Read or ck get-method-source)
        if (/\bcat\s+["']?[^\s]*\.(cs|tsx?)["']?\s*$/.test(cmd) ||
            /\bcat\s+["']?[^\s]*\.(cs|tsx?)["']?\s*\|/.test(cmd)) {
          throw new Error(
            `[ck-guard] Do not use cat to read source files.

Use ck tools to read exactly what you need:

  ${CK} signatures <file>                    # list all members
  ${CK} get-method-source <file> <MemberName> # read one method

Or use the Read tool if you need the full file. cat wastes tokens by dumping
the entire file into the command output without line numbers.`
          )
        }

        // Detect raw grep on source files
        const isGrepCmd = cmd.includes("grep")
        const targetsSource =
          SOURCE_EXT_RE.test(cmd) || /grep\s+-[a-zA-Z]*r/.test(cmd)

        if (isGrepCmd && targetsSource) {
          throw new Error(
            `[ck-guard] bash grep on source files detected.

Follow the code search protocol:
  1. ${CK} find-scope --query "<module, concept, operation, type>"
  2. ${CK} signatures <folder-or-file>
  3. ${CK} get-method-source <file> <MemberName>

Use the native grep tool (not bash grep) only within a scoped folder.`
          )
        }
        return
      }
    },

    // ── tool.execute.after: tight score cluster hint ──────────────────────
    // Fires after ck find-scope completes. Parses the score column
    // and appends a hint when avg_gap = spread/(count-1) <= 0.01 and scores
    // are above the noise floor — scales correctly with --top N.
    "tool.execute.after": async (
      input: { tool: string; sessionID: string; callID: string; args: any },
      output: { title: string; output: string; metadata: any }
    ) => {
      if (input.tool !== "bash") return

      const cmd = String(input.args?.command ?? "")
      if (!/ck\s+find-scope\b/.test(cmd)) return

      // Parse score values from lines formatted as "<float>\t<folder-path>"
      const scoreLineRe = /^([\d.]+)\t/
      const scores: number[] = []
      for (const line of output.output.split("\n")) {
        const m = scoreLineRe.exec(line.trim())
        if (m) {
          const score = parseFloat(m[1])
          if (!isNaN(score)) scores.push(score)
        }
      }

      // Need at least 5 scored results to make a meaningful assessment
      if (scores.length < 5) return

      const maxScore = Math.max(...scores)
      const minScore = Math.min(...scores)
      const spread   = maxScore - minScore
      const avgGap   = spread / (scores.length - 1)

      // Tight cluster: avg gap between adjacent scores <= 0.01 and all above
      // the noise floor (0.70). Using avg_gap rather than a fixed spread
      // threshold makes the check scale with --top N: --top 5 triggers at
      // spread ≤ 0.04, --top 10 at ≤ 0.09, --top 30 at ≤ 0.29.
      if (avgGap <= 0.01 && minScore > 0.70) {
        const suggested = (minScore - avgGap).toFixed(2)
        output.output +=
          `\n[ck-hint] Scores are tightly clustered ` +
          `(${minScore.toFixed(2)}–${maxScore.toFixed(2)} across ${scores.length} folders). ` +
          `The cutoff is likely mid-cluster — relevant folders may be missing. ` +
          `Re-run with --min-score ${suggested} to capture the full cluster.`
      }
    },
  }
}
