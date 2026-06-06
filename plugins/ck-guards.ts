/**
 * ck-guards — OpenCode plugin
 *
 * Enforces the Context King code-search protocol by intercepting anti-patterns
 * before they waste tokens. In OpenCode, throw actually blocks the call.
 *
 * Deployed to: .opencode/plugin/ck-guards.ts
 * Auto-loaded by OpenCode from .opencode/plugin/ on session start.
 *
 * Guards implemented (tool.execute.before — reactive):
 *   read on source files (.cs/.ts/.tsx)            → allowed (use only immediately before editing)
 *   glob on source files across a wide path         → redirect to ck find-files
 *   grep on source files across a wide path         → redirect to ck find-files
 *   bash cat on source files                        → redirect to ck get-method-source / ck read-full-file
 *   broad recursive bash grep from source roots     → redirect to ck find-files
 *   bash pipe on ck find-files / expand-folder      → block (destroys structure)
 *   raw/piped dotnet build loops                     → redirect to ck build-check
 *
 * grep/rg/glob/search are allowed only after scope is established.
 *
 * Proactive hooks (fire before model decides what to call):
 *   tool.definition                     → prepend CK redirect to grep/glob/read descriptions
 *   experimental.chat.system.transform  → inject CK protocol at top of system prompt each turn
 *
 * Hints implemented (tool.execute.after):
 *   ck find-files with tight score cluster → suggest --min-score
 *   signal-based knowledge-capture reminders (with cooldown + ck learn detection)
 */

import * as fs from "node:fs"
import * as path from "node:path"
import { homedir } from "node:os"
import { execSync } from "node:child_process"
import { createHash } from "node:crypto"

function resolveCkCommand(): string {
  const homeCk = path.join(homedir(), ".ck", "bin", "ck")
  const homeCkCmd = path.join(homedir(), ".ck", "bin", "ck.cmd")
  const candidates = [
    homeCk,
    homeCkCmd,
    path.join(process.cwd(), ".opencode", "skills", "ck", "ck"),
    path.join(process.cwd(), ".opencode", "skills", "ck", "ck.cmd"),
    path.join(homedir(), ".config", "opencode", "skills", "ck", "ck"),
    path.join(homedir(), ".config", "opencode", "skills", "ck", "ck.cmd"),
  ]

  // Prefer a CK binary that already exposes read-full-file, since read guards
  // redirect directly to this command.
  for (const candidate of candidates) {
    if (!fs.existsSync(candidate)) continue
    try {
      const help = execSync(`${JSON.stringify(candidate)} --help`, {
        encoding: "utf8",
        stdio: ["ignore", "pipe", "ignore"],
      })
      if (help.includes("read-full-file")) return candidate
    } catch {
      // fall through to next candidate
    }
  }

  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) {
      return candidate
    }
  }

  return "ck"
}

const CK = resolveCkCommand()

// session.idle fires when the agent finishes a turn. We use it to write a
// persistent marker so the NEXT turn's system-prompt injection can remind the
// model to run `ck learn` if it hasn't yet.
let sessionKnowledgeNeeded = false
let sessionKnowledgeSatisfied = false
let knowledgeReminderCount = 0
let lastKnowledgeReminderAtMs = 0
let knowledgeSignalCount = 0
const KNOWLEDGE_REMINDER_COOLDOWN_MS = 90_000
const KNOWLEDGE_REMINDER_MAX = 8

// Stateful anti-loop controls for this OpenCode session.
let pendingKeywordMapQuery: string | null = null
let lastFindFilesCommand: string | null = null
let lastExpandFolderCommand: string | null = null
let noMatchFolder: string | null = null
let noMatchCount = 0
let knownTargetFile: string | null = null
let knownTargetFrom: string | null = null
let expandFolderCount = 0
let signaturesFolderCount = 0
let recentSearchToken: string | null = null
let recentSearchCount = 0
let recentSearchFirstMs = 0
let lastBuildCheckCommand: string | null = null
let lastBuildCheckAtMs = 0
let lastBuildCheckTree: string | null = null
let scopeBootstrapRequired = false
let preScopeSearchViolationCount = 0

const SOURCE_EXT_RE = /\.(cs|tsx?|kt|kts|py)(\b|$)/
const STATE_FILE = path.join(process.cwd(), ".ck-index", ".ck-guard-state.json")
const CK_CONFIG_FILE = path.join(process.cwd(), ".ck.json")
const CK_LEARN_PENDING_FILE = path.join(process.cwd(), ".ck-index", ".ck-learn-pending")

/**
 * A path is considered narrow (already scoped) when it has at least 2 segments.
 * Any path with at least one subfolder — e.g. `src/RetryPolicies` or
 * `src/Modules/Inventory` — is treated as narrow. This covers both deep
 * enterprise repo layouts and shallower OSS layouts.
 *
 * Still blocked as broad:
 *   ""                              → 0 segments, repo root
 *   "."                             → 0 effective segments
 *   "src"                           → 1 segment, too broad to grep .cs/.ts across
 *   "app"                           → 1 segment
 *
 * Treated as narrow (allowed):
 *   "src/RetryPolicies"             → 2 segments
 *   "src/Modules/Inventory"         → 3 segments
 *   "src/Modules/Inventory/Orders"  → 4 segments
 */
function isNarrowPath(path: string | undefined): boolean {
  if (!path) return false
  const segments = path
    .split("/")
    .map((s) => s.trim())
    .filter((s) => s !== "" && s !== ".")
  return segments.length >= 2
}

/**
 * Extract the static path prefix of a glob pattern — everything before the
 * first wildcard character (`*`, `?`, `[`, `{`). Used to detect when a
 * pattern itself encodes a narrow scope, e.g.
 *   "src/ContextKing.Core/Ast/**\/*.cs"  →  "src/ContextKing.Core/Ast"
 *   "src/Modules/Inventory/*.cs"          →  "src/Modules/Inventory"
 *   "**\/*.cs"                            →  ""  (broad)
 *   "*.cs"                                →  ""  (broad)
 */
function globPrefix(pattern: string): string {
  const firstWild = pattern.search(/[*?[{]/)
  const prefix = firstWild < 0 ? pattern : pattern.slice(0, firstWild)
  // Trim trailing slash so "src/Foo/" and "src/Foo" both count as 2 segments.
  return prefix.replace(/\/+$/, "")
}

/**
 * A glob/grep call is narrowly scoped if EITHER the `path` argument OR the
 * static prefix of the pattern identifies a folder at least 2 segments deep.
 */
function isNarrowlyScoped(path: string, pattern: string): boolean {
  return isNarrowPath(path) || isNarrowPath(globPrefix(pattern))
}

function hasGlobMeta(input: string): boolean {
  return /[*?[{]/.test(input)
}

function isExplicitSourceFileTarget(input: string | null | undefined): boolean {
  const normalized = normalizePath(input)
  if (!normalized) return false
  if (hasGlobMeta(normalized)) return false
  return SOURCE_EXT_RE.test(normalized)
}

function normalizePath(p: string | null | undefined): string {
  if (!p) return ""
  return p.replace(/\\/g, "/").replace(/^\.\//, "").replace(/\/+$/, "")
}

function loadScopedFolders(): string[] {
  try {
    if (!fs.existsSync(STATE_FILE)) return []
    const raw = fs.readFileSync(STATE_FILE, "utf8")
    const parsed = JSON.parse(raw) as { scopedFolders?: unknown }
    if (!Array.isArray(parsed.scopedFolders)) return []
    return parsed.scopedFolders
      .filter((x): x is string => typeof x === "string")
      .map((x) => normalizePath(x))
      .filter((x) => x.length > 0)
  } catch {
    return []
  }
}

function isWithinScopedFolders(p: string | null | undefined, scopedFolders: string[]): boolean {
  const target = normalizePath(p)
  if (!target) return false
  return scopedFolders.some((folder) => target === folder || target.startsWith(`${folder}/`))
}

function extractCommandPaths(cmd: string): string[] {
  const paths: string[] = []

  for (const m of cmd.matchAll(/ck\s+signatures\b(?:\s+--\w+(?:\s+"[^"]+"|\s+\S+)*)?\s+"?([^\s"]+)"?/g)) {
    paths.push(m[1])
  }

  for (const m of cmd.matchAll(/\b(?:grep|rg|find)\b(?:\s+--[^\s]+|\s+-[^\s]+)*\s+([./\w-][^\s|;]*)/g)) {
    paths.push(m[1])
  }

  return paths
}

function extractExplicitSourceFiles(cmd: string): string[] {
  const files = new Set<string>()
  for (const m of cmd.matchAll(/(?:^|[\s"'`])([~./\w:-][^\s"'`;|]*\.(?:cs|tsx?))(?:$|[\s"'`;|])/g)) {
    const candidate = normalizePath(m[1])
    if (isExplicitSourceFileTarget(candidate)) files.add(candidate)
  }
  return Array.from(files)
}

function extractSearchTokenFamily(cmd: string): string | null {
  let token: string | null = null

  if (/(^|[;&|\s])(grep|rg)\b/.test(cmd)) {
    token = cmd.match(/(?:grep|rg)[^"]*"([^"]{3,})"/)?.[1] ?? null
    token ??= cmd.match(/(?:grep|rg)[^']*'([^']{3,})'/)?.[1] ?? null
    if (!token) {
      const parts = cmd.split(/\s+/)
      for (let i = 0; i < parts.length; i += 1) {
        if (parts[i] === "grep" || parts[i] === "rg") {
          for (let j = i + 1; j < parts.length; j += 1) {
            if (parts[j].startsWith("-")) continue
            token = parts[j]
            break
          }
        }
        if (token) break
      }
    }
  } else if (/(^|[;&|\s])find\b/.test(cmd)) {
    token = cmd.match(/-name\s+"([^"]{3,})"/)?.[1] ?? null
    token ??= cmd.match(/-name\s+'([^']{3,})'/)?.[1] ?? null
  }

  if (!token) return null
  const normalized = token.toLowerCase().replace(/[^a-z0-9_]+/g, " ").trim()
  if (!normalized) return null
  return normalized.split(/\s+/)[0] ?? null
}

function gitTreeFingerprint(): string {
  try {
    const status = execSync("git status --porcelain --untracked-files=no", {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    })
    return createHash("sha256").update(status).digest("hex")
  } catch {
    return ""
  }
}

function extractFindFilesQuery(cmd: string): string | null {
  const m = cmd.match(/--query\s+"([^"]+)"/)
  return m?.[1] ?? null
}

function extractExpandFolderTarget(cmd: string): string | null {
  const m = cmd.match(/ck\s+expand-folder\b[\s\S]*?\s([^\s]+)\s*(?:2>&1)?\s*$/)
  return m?.[1] ?? null
}

function extractFileArgForTool(cmd: string, toolName: string): string | null {
  const m = cmd.match(new RegExp(`ck\\s+${toolName}\\b\\s+"?([^"\\s]+\\.(?:cs|ts|tsx))"?`))
  return m?.[1] ?? null
}

function isBrainDisabled(): boolean {
  try {
    if (!fs.existsSync(CK_CONFIG_FILE)) return false
    const raw = fs.readFileSync(CK_CONFIG_FILE, "utf8")
    const parsed = JSON.parse(raw) as { brain?: unknown }
    return parsed.brain === false
  } catch {
    return false
  }
}

export default async function ckGuards() {
  return {
    "tool.execute.before": async (
      input: { tool: string },
      output: { args: Record<string, unknown> }
    ) => {
      if (!fs.existsSync(CK_CONFIG_FILE)) return

      const { tool } = input
      const args = output.args

      // ── native read/edit/write are allowed on source files ─────────────────
      // Exploration discipline is enforced via search guards + protocol.
      if (tool === "read") {
        const filePath = String(args.filePath ?? args.path ?? "")
        const normalizedFile = normalizePath(filePath)
        const isSource = SOURCE_EXT_RE.test(normalizedFile)
        if (!isSource) return

        const offset = Number(args.offset ?? 0)
        const limit  = Number(args.limit ?? 0)
        const hasOffset = Number.isFinite(offset) && offset > 0
        const hasLimit  = typeof args.limit !== "undefined"

        // Allow small targeted reads — model already knows the line range.
        // Block only true scrolling: offset with no limit, or offset + large
        // limit (> 150 lines). Reads from the top (no offset) are fine.
        const isScrolling = hasOffset && (!hasLimit || limit > 150)
        if (isScrolling) {
          throw new Error(
            `[ck-guard] Partial source Read with offset/limit is blocked.

Do not scroll source files with Read offsets. Use CK targeted reads:
  ${CK} signatures "${normalizedFile}"
  ${CK} get-method-source "${normalizedFile}" <MemberName>
  ${CK} get-type-source "${normalizedFile}" <TypeName>
  ${CK} get-constructors "${normalizedFile}"
  ${CK} get-usings "${normalizedFile}"
  ${CK} get-base-types "${normalizedFile}"
  ${CK} get-enum-members "${normalizedFile}" <EnumName>

If full-file context is truly required:
  ${CK} read-full-file "${normalizedFile}"
  ${CK} read-full-file --allow-large "${normalizedFile}"`
          )
        }
      }

      // ── glob: broad source file pattern ──────────────────────────────────
      if (tool === "glob") {
        const scopedFolders = loadScopedFolders()
        const pattern = String(args.pattern ?? args.glob ?? "")
        const path = String(args.path ?? args.cwd ?? "")
        const target = path || globPrefix(pattern)
        const isSource = SOURCE_EXT_RE.test(pattern)
        const explicitFileTarget =
          isExplicitSourceFileTarget(path) || isExplicitSourceFileTarget(pattern)

        if (isSource && scopedFolders.length === 0 && !explicitFileTarget) {
          scopeBootstrapRequired = true
          preScopeSearchViolationCount += 1
          throw new Error(
            `[ck-guard] Source search attempted before scope was established (repeat #${preScopeSearchViolationCount}).

Before grep/glob/find-style searching, run:
  ${CK} get-keyword-map --query "<domain concept operation>"
  ${CK} find-files --query "<refined query from keyword-map>" --task "<task intent>"

Then keep all searches inside returned folders.`
          )
        }

        if (isSource && scopedFolders.length > 0 && !explicitFileTarget && !isWithinScopedFolders(target, scopedFolders)) {
          throw new Error(
            `[ck-guard] Glob path is outside current scoped folders from ck find-files.

Keep searches inside:
  ${scopedFolders.join("\n  ")}

If direction changed:
  ${CK} get-keyword-map --query "<new direction>"
  ${CK} find-files --query "<new direction>" --task "<task intent>"`
          )
        }

        if (isSource && !explicitFileTarget && !isNarrowlyScoped(path, pattern)) {
          throw new Error(
            `[ck-guard] Broad source file glob detected (pattern: "${pattern}", path: "${path || "repo root"}").

Use ck find-files to discover the right area first:
  ${CK} find-files --query "<multi-keyword description>" --task "<task intent>"

Then explore within those folders:
  ${CK} expand-folder --pattern "<keyword>" <folder>/
  ${CK} get-method-source <file> <MemberName>

Do NOT use broad glob — it wastes tokens scanning irrelevant files.`
          )
        }
        return
      }

      // ── grep: broad source file search ─────────────────────────────────────
      if (tool === "grep") {
        const scopedFolders = loadScopedFolders()
        const globArg = String(args.glob ?? args.include ?? "")
        const typeArg = String(args.type ?? "")
        const path = String(args.path ?? args.cwd ?? "")
        const isSource =
          SOURCE_EXT_RE.test(globArg) || /^(cs|tsx?)$/.test(typeArg)
        const explicitFileTarget = isExplicitSourceFileTarget(path)

        if (isSource && scopedFolders.length === 0 && !explicitFileTarget) {
          scopeBootstrapRequired = true
          preScopeSearchViolationCount += 1
          throw new Error(
            `[ck-guard] Source search attempted before scope was established (repeat #${preScopeSearchViolationCount}).

Before grep/glob/find-style searching, run:
  ${CK} get-keyword-map --query "<domain concept operation>"
  ${CK} find-files --query "<refined query from keyword-map>" --task "<task intent>"

Then keep all searches inside returned folders.`
          )
        }

        if (scopedFolders.length > 0 && isSource && !explicitFileTarget && !isWithinScopedFolders(path, scopedFolders)) {
          throw new Error(
            `[ck-guard] Grep path is outside current scoped folders from ck find-files.

Keep searches inside:
  ${scopedFolders.join("\n  ")}

If direction changed:
  ${CK} get-keyword-map --query "<new direction>"
  ${CK} find-files --query "<new direction>" --task "<task intent>"`
          )
        }

        if (isSource && !explicitFileTarget && !isNarrowlyScoped(path, globArg)) {
          throw new Error(
            `[ck-guard] Broad source file grep detected (path: "${path || "repo root"}").

Use ck find-files to discover the right area first:
  ${CK} find-files --query "<multi-keyword description>" --task "<task intent>"

Then explore within those folders:
  ${CK} expand-folder --pattern "<keyword>" <folder>/
  ${CK} get-method-source <file> <MemberName>

Do NOT use broad grep — it wastes tokens scanning irrelevant files.`
          )
        }
        return
      }

      // ── bash: piped ck output or grep on source files ───────────────────
      if (tool === "bash") {
        const scopedFolders = loadScopedFolders()
        const cmd = String(args.command ?? "")
        const explicitSourceFiles = extractExplicitSourceFiles(cmd)
        const hasExplicitSourceFileTarget = explicitSourceFiles.length > 0

        // Strip quoted string literals (single, double, and heredoc bodies) so
        // the guard only inspects actual command tokens, not message bodies
        // passed as arguments (e.g. `git commit -m "...grep..."` or
        // `gh release edit --notes-file ...`). Without this, any commit message
        // or release note that mentions `grep` and `.cs`/`.ts` would be blocked.
        const stripped = cmd
          // heredoc bodies: <<EOF ... EOF or <<'EOF' ... EOF
          .replace(/<<-?\s*'?"?(\w+)"?'?[\s\S]*?^\s*\1\s*$/gm, " ")
          // $(...) command substitutions: inspect the outer command only
          .replace(/\$\([^)]*\)/g, " ")
          // double-quoted strings
          .replace(/"(?:\\.|[^"\\])*"/g, " ")
          // single-quoted strings
          .replace(/'(?:\\.|[^'\\])*'/g, " ")

        const isSearchCommand = /(^|[;&|\s])(grep|rg|find)\b/.test(stripped)
        const isSourceSearchCommand =
          isSearchCommand &&
          (SOURCE_EXT_RE.test(stripped) || /(^|[;&|\s])(src|\.\/src|src\/Modules|src\/Hosts)\b/.test(stripped))

        if (scopedFolders.length === 0 && isSourceSearchCommand && !hasExplicitSourceFileTarget) {
          scopeBootstrapRequired = true
          preScopeSearchViolationCount += 1
          throw new Error(
            `[ck-guard] Source search attempted before scope was established (repeat #${preScopeSearchViolationCount}).

Before grep/glob/find-style searching, run:
  ${CK} get-keyword-map --query "<domain concept operation>"
  ${CK} find-files --query "<refined query from keyword-map>" --task "<task intent>"

Then keep all searches inside returned folders.`
          )
        }

        // Strict scope lock after successful find-files.
        if (scopedFolders.length > 0 &&
            /(^|[;&|\s])(grep|rg|find)\b|ck\s+signatures\b/.test(stripped)) {
          for (const commandPath of extractCommandPaths(stripped)) {
            if (isExplicitSourceFileTarget(commandPath)) continue
            if (!isWithinScopedFolders(commandPath, scopedFolders)) {
              throw new Error(
                `[ck-guard] Source search/signatures path is outside current scoped folders.

Keep operations inside:
  ${scopedFolders.join("\n  ")}

If your direction changed:
  ${CK} get-keyword-map --query "<new direction>"
  ${CK} find-files --query "<new direction>" --task "<task intent>"`
              )
            }
          }
        }

        // If the previous find-files was broad, force get-keyword-map before
        // any further find-files/expand-folder call.
        if (pendingKeywordMapQuery &&
            /ck\s+(find-files|expand-folder)\b/.test(stripped) &&
            !/ck\s+get-keyword-map\b/.test(stripped)) {
          throw new Error(
            `[ck-guard] Previous ck find-files was broad/ambiguous.

Run keyword mapping before more scope/explore calls:
  ${CK} get-keyword-map --query "${pendingKeywordMapQuery}"

Then treat keyword-map/session-keyword-atlas as source-of-truth for this direction. Pick 3-7 precision terms (provider/domain + workflow + symbol/DTO/type), then rerun ck find-files once with required --task.`
          )
        }

        // Block exact repeated scope/explore commands.
        if (/ck\s+find-files\b/.test(stripped) && lastFindFilesCommand && cmd === lastFindFilesCommand) {
          throw new Error(
            `[ck-guard] Repeated identical ck find-files command.

Do not rerun the same scope command unchanged. If previous output was broad:
  ${CK} get-keyword-map --query "<same query>"
Then rerun find-files with refined terms and required --task.`
          )
        }

        if (/ck\s+expand-folder\b/.test(stripped) && lastExpandFolderCommand && cmd === lastExpandFolderCommand) {
          throw new Error(
            `[ck-guard] Repeated identical ck expand-folder command.

Refine --pattern using add-keyword-hints instead of rerunning the same command.`
          )
        }

        // After 2 consecutive no-match results in the same folder, block more
        // expand-folder calls in that folder until the search is re-scoped.
        const expandTarget = extractExpandFolderTarget(cmd)
        if (/ck\s+expand-folder\b/.test(stripped) &&
            noMatchFolder &&
            noMatchCount >= 2 &&
            expandTarget &&
            expandTarget.includes(noMatchFolder)) {
          throw new Error(
            `[ck-guard] This folder already had 2 consecutive expand-folder no-match results.

Stop expanding the same folder. Either:
  1) run ${CK} get-keyword-map + refined ${CK} find-files with --task, or
  2) switch to another scoped folder.`
          )
        }

        if (/ck\s+expand-folder\b/.test(stripped) &&
            knownTargetFile) {
          throw new Error(
            `[ck-guard] expand-folder is for uncharted map-building, not after a concrete file target is known in this direction.

Known target from ${knownTargetFrom ?? "targeted-read"}:
  ${knownTargetFile ?? "<unknown>"}

Next step in this direction:
  ${CK} signatures "${knownTargetFile ?? "<file>"}"
  ${CK} get-method-source "${knownTargetFile ?? "<file>"}" <MemberName>

If your direction changed, reset scope explicitly with:
  ${CK} find-files --query "<new direction query>" --task "<task intent>"`
          )
        }

        if (/ck\s+expand-folder\b/.test(stripped) &&
            expandFolderCount >= 3 &&
            !knownTargetFile) {
          throw new Error(
            `[ck-guard] expand-folder map-building budget reached (3 calls for this direction).

Use targeted reads now:
  ${CK} signatures <file.cs>
  ${CK} get-method-source <file.cs> <MemberName>

If still uncharted, reset direction first:
  ${CK} get-keyword-map --query "<same query>"
  ${CK} find-files --query "<refined query>" --task "<task intent>"`
          )
        }

        const tokenFamily = extractSearchTokenFamily(stripped)
        if (tokenFamily &&
            recentSearchToken === tokenFamily &&
            recentSearchCount >= 4 &&
            (Date.now() - recentSearchFirstMs) <= 90_000) {
          throw new Error(
            `[ck-guard] Repeated grep/find loop on token family '${tokenFamily}'.

Switch to targeted symbol search instead:
  ${CK} find-symbol "${tokenFamily}"
  ${CK} refs "${tokenFamily}"

This avoids repeated broad text search churn.`
          )
        }


        // Detect filtering saved Claude/OpenCode tool-result files.
        if (/(\.claude\/projects\/.*\/tool-results\/|\.opencode\/.*tool-results\/)/.test(stripped) &&
            /\|\s*(grep|rg|awk|sed|head|tail|less|more)\b/.test(stripped)) {
          throw new Error(
            `[ck-guard] Do not grep saved tool-result files.

Filtering tool-result files rehydrates previous large outputs and wastes context.
Use the CK command with a narrower pattern instead:

  ${CK} expand-folder --pattern "<keyword>" <folder>
  ${CK} get-method-source <file> <MemberName>`
          )
        }

        // Detect ck find-files piped through content-filtering tools.
        // Allow: head, wc (truncation/counting, harmless).
        // Block: grep, tail, sort, awk, sed, cut (filter or reorder scored results).
        if (/ck\s+find-files\b/.test(stripped) &&
            /\|\s*(tail|grep|sort|awk|sed|cut|less|more)\b/.test(stripped)) {
          throw new Error(
            `[ck-guard] Do NOT pipe ck find-files through grep, sort, or awk.

ck find-files output is already ranked by relevance score. Filtering or sorting
destroys that structure. Instead:

  • Reduce output with --top <n> or --min-score <f>

Remove the pipe and run the ck command directly.`
          )
        }

        // Detect ck expand-folder piped through filters/truncation. The command
        // already performs output-aware broad-match refusal and keyword hints.
        if (/ck\s+expand-folder\b/.test(stripped) &&
            /\|\s*(head|tail|grep|rg|sort|awk|sed|cut|less|more|wc)\b/.test(stripped)) {
          throw new Error(
            `[ck-guard] Do NOT pipe ck expand-folder output.

ck expand-folder refuses broad output and prints keyword hints. Filtering or
truncating the output hides that guidance. Rerun directly with a precise pattern:

  ${CK} expand-folder --pattern "<provider>|<workflow>|<symbol>" <folder>`
          )
        }

        // Detect dotnet build piped through post-filters.
        if (/\bdotnet\s+build\b/.test(stripped) &&
            /\|\s*(tail|grep|rg|awk|sed|head|less|more|cut|sort)\b/.test(stripped)) {
          throw new Error(
            `[ck-guard] dotnet build output is being post-filtered via shell pipes.

Use compact verification directly:
  ${CK} build-check <project.csproj>

This runs dotnet build -v q and emits concise diagnostics without tail/grep churn.`
          )
        }

        // Block raw dotnet build by default to avoid duplicate verification loops.
        // Explicit fallback is still available with CK_ALLOW_RAW_BUILD=1.
        if (/\bdotnet\s+build\b/.test(stripped) &&
            !/\bCK_ALLOW_RAW_BUILD=1\b/.test(stripped)) {
          throw new Error(
            `[ck-guard] Raw dotnet build detected.

Raw dotnet build often creates duplicate verification loops. Prefer:
  ${CK} build-check <project.csproj>

If you must run raw dotnet build for troubleshooting:
  CK_ALLOW_RAW_BUILD=1 dotnet build <project.csproj> -v q`
          )
        }

        if (/ck\s+build-check\b/.test(stripped)) {
          const currentTree = gitTreeFingerprint()
          if (lastBuildCheckCommand &&
              stripped === lastBuildCheckCommand &&
              currentTree === (lastBuildCheckTree ?? "") &&
              (Date.now() - lastBuildCheckAtMs) <= 45_000) {
            throw new Error(
              `[ck-guard] Repeated identical ck build-check with no workspace change.

Prefer delta verification:
  ${CK} build-check --delta <project.csproj>

or continue coding before rerunning build-check.`
            )
          }
        }

        // Detect find -exec cat / find | xargs cat (bulk file read)
        if (/\bfind\b/.test(stripped) &&
            (/-exec\s+cat\b/.test(stripped) || /\|\s*xargs\s+cat\b/.test(stripped))) {
          throw new Error(
            `[ck-guard] Bulk file read via find detected.

Use ck tools to read exactly what you need:

  ${CK} expand-folder --pattern "<keyword>" <folder>/                    # list all members in a folder
  ${CK} get-method-source <file> <MemberName>   # read one method

Bulk-reading files via find bypasses targeted reads and wastes tokens.`
          )
        }

        // Detect broad source-tree find used as manual navigation.
        if (/\bfind\s+(?:[^|;]*\s)?(?:src|\.\/src|src\/Modules|src\/Hosts)(?:\s|\/)/.test(stripped) &&
            /(?:-name\s+|--name\s+|-type\s+[fd])/.test(stripped)) {
          throw new Error(
            `[ck-guard] Broad find over source folders detected.

Plain find across src/ returns unranked paths and often floods context. Use:

  ${CK} find-files --query "<domain concept operation>" --task "<task intent>"
  ${CK} expand-folder --pattern "<keyword>" <returned-folder>

If you already know the exact narrow folder, run find inside that folder only.`
          )
        }

        // Detect broad recursive grep/rg from source/module roots.
        if (/(^|[;&|\s])(grep|rg)\b/.test(stripped) &&
            /(^|\s)-[A-Za-z]*r[A-Za-z]*\b|\b-rn\b|\b--recursive\b|\brg\b/.test(stripped) &&
            /(^|\s)(src|\.\/src|src\/Modules|src\/Modules\/[^/\s]+|src\/Hosts|src\/Hosts\/[^/\s]+)\/?(\s|$)/.test(stripped) &&
            /(--include=.*\.(cs|ts|tsx)|\.(cs|ts|tsx)\b|grep|rg)/.test(stripped)) {
          throw new Error(
            `[ck-guard] Broad recursive grep over source/module root detected.

Recursive grep from src/ or a module root scans too much. Use CK to narrow first:

  ${CK} find-files --query "<domain concept operation>" --task "<task intent>" --explain
  ${CK} expand-folder --pattern "<keyword>" <returned-folder>

If you already have focused folders, grep only those exact folders.`
          )
        }

        // Detect cat on source files (should use Read or ck get-method-source)
        if (/\bcat\s+["']?[^\s]*\.(cs|tsx?)["']?\s*$/.test(stripped) ||
            /\bcat\s+["']?[^\s]*\.(cs|tsx?)["']?\s*\|/.test(stripped)) {
          throw new Error(
            `[ck-guard] Do not use cat to read source files.

Use ck tools to read exactly what you need:

  ${CK} signatures <file>                    # list all members
  ${CK} get-method-source <file> <MemberName> # read one method
  ${CK} read-full-file <file>                # full file with guardrail

If you need to modify source, use native Read + Edit/Write in a tight loop
on the target file, or use bash patching when needed.

cat wastes tokens by dumping entire files into command output without line numbers.`
          )
        }

        return
      }
    },

    // ── tool.execute.after: tight score cluster hint ──────────────────────
    // Fires after ck find-files completes. Parses the score column
    // and appends a hint when avg_gap = spread/(count-1) <= 0.01 and scores
    // are above the noise floor — scales correctly with --top N.
    "tool.execute.after": async (
      input: { tool: string; sessionID: string; callID: string; args: any },
      output: { title: string; output: string; metadata: any }
    ) => {
      if (!fs.existsSync(CK_CONFIG_FILE)) return
      if (input.tool !== "bash") return

      const cmd = String(input.args?.command ?? "")

      if (/ck\s+get-keyword-map\b/.test(cmd)) {
        pendingKeywordMapQuery = null
        noMatchFolder = null
        noMatchCount = 0
        knownTargetFile = null
        knownTargetFrom = null
        expandFolderCount = 0
        signaturesFolderCount = 0
      }

      // Explicit completion: once ck learn succeeds in this session, stop
      // reminding unless fresh strong signals appear later.
      if (/ck\s+learn\b/.test(cmd) && !/\b(ERROR|Error)\b/.test(output.output)) {
        sessionKnowledgeSatisfied = true
        sessionKnowledgeNeeded = false
        try { if (fs.existsSync(CK_LEARN_PENDING_FILE)) fs.unlinkSync(CK_LEARN_PENDING_FILE) } catch {}
      }

      if (/ck\s+find-files\b/.test(cmd)) {
        lastFindFilesCommand = cmd
        if (output.output.includes("[ck find-files] Scope is too broad or ambiguous.")) {
          pendingKeywordMapQuery = extractFindFilesQuery(cmd) ?? "<same query>"
        } else {
          scopeBootstrapRequired = false
          preScopeSearchViolationCount = 0
          pendingKeywordMapQuery = null
          noMatchFolder = null
          noMatchCount = 0
        }
        knownTargetFile = null
        knownTargetFrom = null
        expandFolderCount = 0
        signaturesFolderCount = 0
      }

      if (/ck\s+expand-folder\b/.test(cmd)) {
        expandFolderCount += 1
        lastExpandFolderCommand = cmd
        const noMatch = output.output
          .split("\n")
          .find((line) => line.includes("[ck expand-folder] No signatures matched pattern"))
        if (noMatch) {
          const m = noMatch.match(/ in '([^']+)'/)
          const folder = m?.[1] ?? null
          if (folder) {
            if (noMatchFolder === folder) noMatchCount += 1
            else {
              noMatchFolder = folder
              noMatchCount = 1
            }
          }
        }
      }

      const tokenFamily = extractSearchTokenFamily(cmd)
      if (tokenFamily) {
        const now = Date.now()
        if (recentSearchToken === tokenFamily && (now - recentSearchFirstMs) <= 90_000) {
          recentSearchCount += 1
        } else {
          recentSearchToken = tokenFamily
          recentSearchCount = 1
          recentSearchFirstMs = now
        }
      }

      if (/ck\s+build-check\b/.test(cmd)) {
        lastBuildCheckCommand = cmd
        lastBuildCheckAtMs = Date.now()
        lastBuildCheckTree = gitTreeFingerprint()
      }

      // Strong CK exploration signals that imply knowledge might be worth
      // capturing at the end of the ongoing work chunk.
      const isStrongKnowledgeSignal =
        /ck\s+(find-files|expand-folder|signatures|find-symbol|refs|get-method-source|get-type-source|get-constructors|get-usings|get-base-types|get-enum-members)\b/.test(cmd) &&
        !/\b(ERROR|Error)\b/.test(output.output)
      if (isStrongKnowledgeSignal) {
        knowledgeSignalCount += 1
        sessionKnowledgeNeeded = true
        sessionKnowledgeSatisfied = false
      }

      if (/ck\s+get-method-source\b/.test(cmd) && !/\b(ERROR|Error)\b/.test(output.output)) {
        const file = extractFileArgForTool(cmd, "get-method-source")
        if (file) {
          knownTargetFile = file
          knownTargetFrom = "get-method-source"
        }
      }

      if (/ck\s+(get-constructors|get-usings|get-base-types|get-type-source|get-enum-members)\b/.test(cmd) && !/\b(ERROR|Error)\b/.test(output.output)) {
        const file =
          extractFileArgForTool(cmd, "get-constructors") ??
          extractFileArgForTool(cmd, "get-usings") ??
          extractFileArgForTool(cmd, "get-base-types") ??
          extractFileArgForTool(cmd, "get-type-source") ??
          extractFileArgForTool(cmd, "get-enum-members")
        if (file) {
          knownTargetFile = file
          knownTargetFrom = "file-ast-read"
        }
      }

      if (/ck\s+signatures\b/.test(cmd) && !/\b(ERROR|Error)\b/.test(output.output)) {
        const file = extractFileArgForTool(cmd, "signatures")
        if (file) {
          knownTargetFile = file
          knownTargetFrom = "signatures-file"
        } else {
          signaturesFolderCount += 1
        }
      }

      if (/ck\s+find-files\b/.test(cmd)) {
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

        if (scores.length >= 5) {
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
        }

        // After a successful find-files (has scored results), inject the mandatory
        // next-step reminder: run ck recall before any method-body reads.
        const hasResults = scores.length > 0
        if (hasResults && !output.output.includes("[ck find-files] Scope is too broad")) {
          const topFolder = output.output
            .split("\n")
            .map((l) => l.match(/^[\d.]+\t(.+)$/)?.[1])
            .find((v) => v != null) ?? "<top-folder>"
          output.output +=
            `\n[ck-hint] Step 2.5 (mandatory before any method read): ` +
            `\`${CK} recall --folder ${topFolder}\` ` +
            `— run once per folder you intend to read. Silent output = no knowledge yet, proceed normally.`
        }
      }

      // Signal-based knowledge-capture reminders with cooldown and max count.
      // This behaves closer to session-end prompting despite OpenCode not
      // exposing a dedicated stop hook.
      if (!isBrainDisabled() && sessionKnowledgeNeeded && !sessionKnowledgeSatisfied) {
        const now = Date.now()
        const cooldownPassed = (now - lastKnowledgeReminderAtMs) >= KNOWLEDGE_REMINDER_COOLDOWN_MS
        const isCheckpoint =
          /ck\s+(build-check|get-method-source|get-type-source|find-symbol|refs)\b/.test(cmd) ||
          /ck\s+find-files\b/.test(cmd)

        const shouldRemind =
          knowledgeReminderCount < KNOWLEDGE_REMINDER_MAX &&
          cooldownPassed &&
          ((knowledgeReminderCount === 0 && knowledgeSignalCount >= 2) || isCheckpoint)

        if (shouldRemind) {
          knowledgeReminderCount += 1
          lastKnowledgeReminderAtMs = now
          output.output +=
            `\n[ck-hint] Knowledge capture (MUST before final response): run ` +
            `\`${CK} learn --content "<2-4 sentences>" --folders "<folder1,folder2>" --tags "<keywords>"\`. ` +
            `Record non-obvious WHY only (constraints, architecture, cross-module behavior). ` +
            `If nothing non-obvious was learned, skip.`
        }
      }
    },

    // ── tool.definition: bias model away from grep/glob/read for source files ──
    // Fires when the LLM's tool list is being built. Prepending a CK redirect
    // to grep/glob descriptions makes the model reach for CK tools first,
    // before it ever decides to call grep.
    "tool.definition": async (
      input: { toolID: string },
      output: { description: string; parameters: any }
    ) => {
      if (!fs.existsSync(CK_CONFIG_FILE)) return

      if (input.toolID === "grep" || input.toolID === "glob") {
        output.description =
          `[CK PROTOCOL — mandatory for C#, TypeScript, Kotlin, Python source files] ` +
          `Do NOT call this tool for source file search. Use CK tools instead:\n` +
          `  ${CK} get-keyword-map --query "<terms>"           (step 0 — always first)\n` +
          `  ${CK} find-files --query "<terms>" --task "<task intent>"  (step 1 — establishes scope)\n` +
          `  ${CK} expand-folder --pattern "<kw>" <folder>     (step 2 — explore)\n` +
          `grep/glob are only allowed within folders returned by find-files.\n\n` +
          output.description
      }

      if (input.toolID === "read") {
        output.description =
          `[CK PROTOCOL] For C#, TypeScript, Kotlin, Python exploration prefer CK targeted reads:\n` +
          `  ${CK} signatures <file>  |  ${CK} get-method-source <file> <Method>  |  ${CK} read-full-file <file>\n` +
          `Native read is allowed only immediately before editing a known target file — not for exploration.\n\n` +
          output.description
      }
    },

    // ── experimental.chat.system.transform: inject CK preference at top of system prompt ──
    // Fires each turn. unshift() puts the instruction before the static
    // instructions file, giving it higher priority than explicit user-prompt
    // instructions like "use CallGraph before grep."
    "experimental.chat.system.transform": async (
      input: { sessionID?: string; model: any },
      output: { system: string[] }
    ) => {
      if (!fs.existsSync(CK_CONFIG_FILE)) return

      const pendingLearn =
        !isBrainDisabled() && fs.existsSync(CK_LEARN_PENDING_FILE)
          ? ` A previous turn ended with CK tools used but no \`ck learn\` — run it before finishing.`
          : ""

      output.system.unshift(
        `[Context King Protocol — highest priority] This repository has CK initialized. ` +
        `For ANY C#, TypeScript, Kotlin, Python source search: ` +
        `step 0 = \`${CK} get-keyword-map --query "..."\`, ` +
        `step 1 = \`${CK} find-files --query "..." --task "..."\`. ` +
        `These steps are MANDATORY before grep, glob, find, or native Read for exploration. ` +
        `When any other tool (CallGraph, language servers, etc.) returns empty results or fails, ` +
        `the NEXT step is CK tools — never raw grep. ` +
        `grep is only allowed inside folders already returned by find-files. ` +
        `MANDATORY: if CK tools were used this session, run \`${CK} learn\` before the final response.` +
        pendingLearn
      )
    },

    // ── event: session.idle → write pending-learn marker ─────────────────────
    // session.idle fires when the agent finishes a turn. If CK tools were used
    // but ck learn was not run, write a marker so the next turn's system-prompt
    // injection can remind the model.
    "event": async (input: { event: any }) => {
      if (input.event?.type !== "session.idle") return
      if (!fs.existsSync(CK_CONFIG_FILE)) return
      if (!sessionKnowledgeNeeded || sessionKnowledgeSatisfied) return
      if (isBrainDisabled()) return
      try { fs.writeFileSync(CK_LEARN_PENDING_FILE, String(Date.now()), "utf8") } catch {}
    },
  }
}
