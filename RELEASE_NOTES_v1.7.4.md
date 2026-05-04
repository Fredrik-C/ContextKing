## What's Fixed

- Fixed `install-global.sh` local asset detection so `curl ... | bash` no longer accidentally installs binaries from the current working directory when run inside a local clone.
- Updated CLI reported version to `1.7.4`.

## Why This Release

`v1.7.3` assets were shipping stale binaries. This release ensures fresh binaries and correct installer behavior.
