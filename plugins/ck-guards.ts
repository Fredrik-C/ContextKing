/**
 * ck-guards — OpenCode plugin
 *
 * Enforces the Context King code-search protocol by intercepting broad C# file
 * searches before they waste tokens scanning the wrong files.
 *
 * Deployed to: .opencode/plugin/ck-guards.ts
 * Auto-loaded by OpenCode from .opencode/plugin/ on session start.
 *
 * Guards implemented (all non-blocking in intent — throw redirects the agent):
 *   glob on .cs across a wide path   → redirect to ck find-scope
 *   grep on .cs across a wide path   → redirect to ck find-scope
 *   bash grep targeting .cs files    → redirect to ck find-scope
 *
 * Read guard is intentionally omitted: OpenCode only supports throw-to-block
 * with no warn-and-allow equivalent. Blocking all .cs reads would cause infinite
 * retry loops when the agent legitimately needs a full file. The protocol file
 * and AGENTS.md pointer handle read discipline via instructions instead.
 */

const CK = ".opencode/skills/ck/ck"

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

      // ── glob: broad .cs pattern ─────────────────────────────────────────────
      if (tool === "glob") {
        const pattern = String(args.pattern ?? args.glob ?? "")
        const path = String(args.path ?? args.cwd ?? "")

        if (pattern.includes(".cs") && !isNarrowPath(path)) {
          throw new Error(
            `[ck-guard] Broad .cs glob detected (pattern: "${pattern}", path: "${path || "repo root"}").

Run ck find-scope FIRST to narrow scope:
  ${CK} find-scope --query "<multi-keyword description — module, concept, operation, type>"

Then scope this glob to the returned folder path.
Proceed only once the scope is narrowed to a specific folder.`
          )
        }
        return
      }

      // ── grep: broad .cs search ──────────────────────────────────────────────
      if (tool === "grep") {
        const globArg = String(args.glob ?? args.include ?? "")
        const typeArg = String(args.type ?? "")
        const path = String(args.path ?? args.cwd ?? "")
        const isCS = globArg.includes(".cs") || typeArg === "cs"

        if (isCS && !isNarrowPath(path)) {
          throw new Error(
            `[ck-guard] Broad C# grep detected (path: "${path || "repo root"}").

Run ck find-scope FIRST to narrow scope:
  ${CK} find-scope --query "<multi-keyword description — module, concept, operation, type>"

Then scope this grep to the returned folder path.
Proceed only once the scope is narrowed to a specific folder.`
          )
        }
        return
      }

      // ── bash: grep on .cs files ─────────────────────────────────────────────
      if (tool === "bash") {
        const cmd = String(args.command ?? "")
        const isGrepCmd = cmd.includes("grep")
        const targetsCS =
          cmd.includes(".cs") || /grep\s+-[a-zA-Z]*r/.test(cmd)

        if (isGrepCmd && targetsCS) {
          throw new Error(
            `[ck-guard] bash grep on C# files detected.

Do NOT use bash grep to search this codebase — follow the code search protocol:

  1. ${CK} find-scope --query "<module, concept, operation, type>"
  2. ${CK} signatures <file.cs> [file2.cs ...]
  3. ${CK} get-method-source <file.cs> <MemberName>

Use the native grep tool (not bash grep) only within a scoped folder.`
          )
        }
        return
      }
    },
  }
}
