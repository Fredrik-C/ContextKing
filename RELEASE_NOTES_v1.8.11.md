## Focused keyword-map guidance

- `ck get-keyword-map` now emits one compact, copyable `ck find-files` command by default. Use `--verbose` only when diagnostic keyword evidence is needed.
- Suggested searches keep the original query terms, avoid duplicating required terms, and cap the initial result set at eight files to prevent low-signal tail results from overwhelming agents.
- Quoted multiword `--must` values now boost every keyword during lexical retrieval, so constraints such as `--must "price calculate"` work as intended.

## Install

Step 1 — global install (once per machine):

**Mac / Linux:**

```bash
curl -fsSL https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.sh | bash
```

**Windows (PowerShell 7+):**

```powershell
irm https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.ps1 | iex
```

Step 2 — initialize each repository:

```bash
cd /path/to/your-repo
ck init
```

To uninstall, use the matching `uninstall-global.sh` or `uninstall-global.ps1` asset from the latest release.
