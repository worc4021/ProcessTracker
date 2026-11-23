---
description: Remove the user-only copy of ProcessTracker
argument-hint: "[purge-data]"
---
Remove the current user's user-only copy of ProcessTracker.

1. Find `proctrack.exe`: use `proctrack` if it is on the PATH; otherwise use the highest-version
   `%LOCALAPPDATA%\Programs\ProcessTracker\app-<version>\bin\proctrack.exe`. If neither exists, tell the user that no
   user-only copy is installed and stop.
2. Run `proctrack user uninstall`. Add `--purge-data` only if the user asked to delete ProcessTracker's data as well
   (for example with `/process-tracker:uninstall purge-data`); without it the data in `%LOCALAPPDATA%\ProcessTracker`
   is kept. Arguments given: $ARGUMENTS
3. Report the command's output to the user verbatim, together with its exit code. Do not retry it, and never run it
   elevated.

This removes only the user-only copy. The machine-wide service is managed by an administrator and is not touched.
