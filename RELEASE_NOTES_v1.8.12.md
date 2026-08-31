## More useful keyword-map drill-down hints

- `ck get-keyword-map` now shows up to three related keyword hints for follow-up searches instead of merely echoing the original query.
- Related hints prioritize vocabulary from indexed type names over method names, making them more useful as code-navigation anchors.
- When the local embedding model is available, the command also shows up to three semantic keyword hints from the most similar indexed folders.

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
