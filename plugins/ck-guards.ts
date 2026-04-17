/**
 * ck-guards — OpenCode plugin
 *
 * Enforces the Context King code-search protocol by intercepting broad source file
 * searches (.cs, .ts, .tsx) before they waste tokens scanning the wrong files.
 *
 * Deployed to: .opencode/plugin/ck-guards.ts
 * Auto-loaded by OpenCode from .opencode/plugin/ on session start.
 *
 * Guards implemented (all non-blocking in intent — throw redirects the agent):
 *   glob on source files across a wide path   → redirect to ck find-scope
 *   grep on source files across a wide path   → redirect to ck find-scope
 *   bash grep targeting source files          → redirect to ck find-scope
 *
 * Read guard is intentionally omitted: OpenCode only supports throw-to-block
 * with no warn-and-allow equivalent. Blocking all source reads would cause infinite
 * retry loops when the agent legitimately needs a full file. The protocol file
 * and AGENTS.md pointer handle read discipline via instructions instead.
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

Use ck search to find what you need with semantic scoping:
  ${CK} search --query "<domain description>" --pattern "<keyword>"

If you don't have a keyword yet, use ck find-scope to discover the right area:
  ${CK} find-scope --query "<multi-keyword description>"

Do NOT use broad glob/grep — it wastes tokens scanning irrelevant files.`
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

Use ck search to find what you need with semantic scoping:
  ${CK} search --query "<domain description>" --pattern "<keyword>"

If you don't have a keyword yet, use ck find-scope to discover the right area:
  ${CK} find-scope --query "<multi-keyword description>"

Do NOT use broad glob/grep — it wastes tokens scanning irrelevant files.`
          )
        }
        return
      }

      // ── bash: piped ck output or grep on source files ───────────────────
      if (tool === "bash") {
        const cmd = String(args.command ?? "")

        // Detect ck commands piped through head/grep/tail
        if (/ck\s+(search|find-scope|signatures|get-method-source)\b.*\|/.test(cmd)) {
          throw new Error(
            `[ck-guard] Do NOT pipe ck output through head, grep, or tail.

ck output is already structured and scoped. Piping discards folder scores and
grouping structure you need. Instead:

  • Reduce output with --top <n> or --min-score <f>
  • Use --type and --name for precise matching:
    ${CK} search --query "<scope>" --type class --name "ClassName"
  • Types: class, method, member, file

Remove the pipe and run the ck command directly.`
          )
        }

        // Detect raw grep on source files
        const isGrepCmd = cmd.includes("grep")
        const targetsSource =
          SOURCE_EXT_RE.test(cmd) || /grep\s+-[a-zA-Z]*r/.test(cmd)

        if (isGrepCmd && targetsSource) {
          throw new Error(
            `[ck-guard] bash grep on source files detected.

Do NOT use bash grep to search this codebase. Use ck search instead:

  ${CK} search --query "<domain description>" --pattern "<keyword>"

Or follow the full protocol:
  1. ${CK} find-scope --query "<module, concept, operation, type>"
  2. ${CK} signatures <folder-or-file>
  3. ${CK} get-method-source <file> <MemberName>

Use the native grep tool (not bash grep) only within a scoped folder.`
          )
        }
        return
      }
    },
  }
}
