## CK Code Search Protocol — mandatory

Use CK commands for all code navigation. Never use bare `grep -r`, `Glob **/*.cs`, or `Read` on unknown files.

### When → Command

| # | When you need to… | Command |
|---|---|---|
| 1 | Find a class/interface by name | `ck search --query "domain area" --type class --name "IPaymentGateway"` |
| 2 | Find a method by name | `ck search --query "domain area" --type method --name "ChargePayment"` |
| 3 | Find a file by name | `ck search --query "domain area" --type file --name "AdyenGateway"` |
| 4 | Find a property/field by name | `ck search --query "domain area" --type member --name "RefundAmount"` |
| 5 | Search with raw regex (rare) | `ck search --query "domain area" --pattern "Status\.(Active\|Closed)"` |
| 6 | Discover folders (no symbol yet) | `ck find-scope --query "adyen terminal refund card-present"` |
| 7 | List all members in a folder/file | `ck signatures <folder-or-file>` |
| 8 | Read one method body | `ck get-method-source <file> <ExactMemberName>` |
| 9 | Read a small file (<50 lines) | `Read` tool directly — skip signatures/get-method-source |
| 10 | Read 3+ members from one file | `Read` tool — cheaper than 3 separate get-method-source calls |

### Sample workflows

**Find and read a specific symbol:**
```
ck search --query "payment terminal" --type class --name "ITerminalGateway"
ck get-method-source <file-from-result> ChargePaymentAsync
```

**Explore an unknown area:**
```
ck find-scope --query "adyen terminal card-present refund"
ck signatures <top-folder>/
ck get-method-source <file> <member-from-signatures>
```

**Implement a feature using an existing pattern (e.g. add Adyen support matching Stripe):**
```
ck search --query "stripe terminal refund" --type class --name "StripeTerminalGateway"
ck signatures <stripe-folder>/
ck get-method-source <file> RefundPaymentAsync        # read the pattern
ck search --query "adyen terminal" --type class --name "AdyenTerminalGateway"
ck signatures <adyen-folder>/                          # see what exists
ck get-method-source <file> <relevant-method>          # read current impl
# now edit
```

### Rules

1. **`--type` + `--name` is the fast path** — skips index loading, searches repo-wide instantly. Always prefer over `--pattern`.
2. **Don't guess symbol names** for `--name`. If you don't know the exact name, run `ck signatures` on the folder first to discover members.
3. **Don't re-search the same concept.** Rephrasing `--query` with synonyms returns the same folders. Change `--name`/`--type` instead, or move to signatures.
4. **Don't pipe CK output** through `grep`, `head`, or `tail`. Use `--top` or `--min-score` instead. Piped commands will be blocked.
5. **Budget: 6 searches per task.** One search per distinct concept. Past 8 you are wasting context — switch to `ck signatures`.
6. **`get-method-source` needs the exact name** from `ck signatures` output. `Refund` won't match `RefundPaymentAsync`.
7. Use **multi-keyword** `--query`: `"adyen terminal card-present refund"` not `"adyen"`.
