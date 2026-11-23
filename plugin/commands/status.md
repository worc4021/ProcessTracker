---
description: Show whether ProcessTracker runs for you, and the current machine load
---
Show the user the state of ProcessTracker on this machine.

1. Find `proctrack.exe`: use `proctrack` if it is on the PATH; otherwise use the highest-version
   `%LOCALAPPDATA%\Programs\ProcessTracker\app-<version>\bin\proctrack.exe`. If neither exists, say that ProcessTracker
   is not installed and that `/process-tracker:install` installs a user-only copy without admin rights, then stop.
2. Run `proctrack user status`, then `proctrack load`.
3. Report both outputs to the user verbatim. Change nothing: do not install, start, stop or uninstall anything.
