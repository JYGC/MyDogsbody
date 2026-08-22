# Walk fixtures — built at test setup, not committed

Per tasks.md 1.4: "build the junction at test setup rather than committing it — Git cannot carry
one." The same reasoning applies to the other two traps this folder would otherwise hold, so
none of the three below are committed files. Phase 3's `ThunderbirdFolderScannerTests.fs`
constructs each at test setup and tears it down afterward:

- **An unreadable directory** — create a temp directory and deny read access to it (e.g. via
  `DirectorySecurity` / ACLs on Windows, or by holding it open exclusively), then assert the
  walk records it in `Unreadable` and continues past it rather than aborting.
- **A directory junction pointing at an ancestor** — `Directory.CreateSymbolicLink` (or the
  `mklink /J` equivalent via a junction-creation helper) from a descendant back up to an
  ancestor of itself, then assert the walk does not loop — it must track visited directories by
  their canonical (resolved) path, not by the path string it was reached through.
- **A tree deeper than the depth bound** — `Directory.CreateDirectory` a chain of nested
  directories exceeding the scanner's depth bound (12, per design.md), then assert the walk
  stops descending rather than continuing indefinitely or throwing.

Keeping these out of git avoids committing a broken symlink/junction (which most tools mishandle
on checkout/clone) and an ACL-denied directory (which depends on the account doing the checkout).
