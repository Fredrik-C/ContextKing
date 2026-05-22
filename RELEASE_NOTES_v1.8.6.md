## Install
Step 1 — global install (once per machine):

Mac / Linux:
`curl -fsSL https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.sh | bash`

Windows (PowerShell 7+):
`irm https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.ps1 | iex`

Step 2 — initialize each repo (once per repo):
```
cd /path/to/your-repo
ck init
```

## Highlights

- Added explicit lexical-query guidance for both `ck find-files` and `ck get-keyword-map`.
- Clarified that query text should map to indexed code lexicals (path segments, file-name tokens, type names, and member/method words), not natural-language questions.
- Improved protocol consistency across skills, rules, and command reference documentation.

## Improvements

- `skills/ck-find-files/SKILL.md`
  - Added a `Query Wording (Important)` section with concrete good/weak query examples.
  - Documented preferred 3-7 high-signal lexical terms (domain + workflow + operation/symbol).

- `skills/ck-get-keyword-map/SKILL.md`
  - Added matching lexical-query guidance and examples for `--query`.
  - Clarified that keyword expansion quality depends on lexical anchors present in indexed metadata.

- `rules/ck-code-search-protocol.md`
  - Added mandatory lexical-query wording rule for `ck find-files --query`.
  - Added explicit note that `ck get-keyword-map --query` follows the same lexical rule.

- `README.md`
  - Updated command references for `ck find-files` and `ck get-keyword-map` to reinforce lexical query phrasing.
