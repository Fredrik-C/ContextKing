---
name: ck-search
description: Preferred entry point for code navigation. Scoped keyword search combining semantic folder ranking with git grep. Use this FIRST when you have a symbol, method name, or keyword to find.
---

# ck search — Reference

## Syntax

```bash
# Typed search (preferred — instant fast path)
ck search --query "<scope>" --type <class|method|member|file> --name "<symbol>"

# Raw regex (fallback)
ck search --query "<scope>" --pattern "<regex>"
```

## Types

| Type | Matches | Example |
|---|---|---|
| `class` | class, interface, struct, record, enum declarations | `--type class --name ITerminalGateway` |
| `method` | method declarations and calls | `--type method --name ChargePayment` |
| `member` | property, field declarations | `--type member --name RefundAmount` |
| `file` | filenames (no content grep) | `--type file --name AdyenGateway` |

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | required | Semantic scope — multi-keyword domain description |
| `--name <symbol>` | — | Symbol name (generates language-aware pattern) |
| `--type <kind>` | — | Symbol type (see table above) |
| `--pattern <regex>` | — | Raw regex (cannot combine with --name/--type) |
| `--top <n>` | 10 | Folders to search within |
| `--min-score <f>` | off | Score threshold |
| `--case-sensitive` | off | Exact case matching |

## Output

```
<score>\t<folder-path>
  <file>:<line>: <matching-content>
```

Grouped by folder, ordered by relevance. No matches → searched folders printed to stderr.

## Behaviour

- **`--type` + `--name`**: repo-wide git grep first (fast path, no index needed). Falls back to scoped search only if nothing found.
- **`--pattern`**: always uses index for folder ranking.
- Auto-builds index on first scoped search if missing.
- Case-insensitive by default.
