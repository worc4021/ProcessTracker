---
description: Install a user-only copy of ProcessTracker (no admin rights needed)
---
Install ProcessTracker for the current Windows user. Run this command once, with your shell tool, exactly as written:

```
powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/scripts/install-user.ps1"
```

Then report the command's output to the user verbatim, together with its exit code.

Rules:
- Run it once. If it fails or refuses, do not retry it, and never retry it elevated (no `Start-Process -Verb RunAs`, `runas`, `gsudo` or similar). The script refuses elevation on purpose: the user-only copy belongs to the signed-in user.
- Do not try to install ProcessTracker any other way (no winget, MSI or manual download) unless the user asks for it.
- If the output says the machine-wide service is in use, nothing more is needed.
