---
name: ck-get-usings
description: List all using directives (C#) or import statements (TypeScript/TSX) in a file. Use when adding a new dependency and you need to know what is already imported.
---

# ck get-usings — Reference

## Syntax

```bash
.claude/skills/ck/ck get-usings <file>
```

## Output

Plain text, one directive per line in source order:

**C#:**
```
using System;
using System.Collections.Generic;
using Mews.Accounting.Core.Payments;
```

**TypeScript:**
```
import React from 'react';
import { useState, useEffect } from 'react';
import type { PaymentGateway } from '../types';
```

## When to use

- Before adding a new `using` or `import` to a file you plan to edit
- To check if a namespace/package is already imported without reading the whole file
- Avoids a full `ck read-full-file` just to see the imports section

## Notes

- No index required — always reads live from disk.
- For C#, captures both top-level and namespace-scoped using directives.
