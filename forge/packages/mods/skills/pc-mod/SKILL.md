---
name: pc-mod
description: Checks the user's PC before 4GE builds or launches anything. Use when a build needs a provider CLI, Docker, a free port or a stored credential, or when the user asks what is installed and signed in on this PC.
license: Proprietary
compatibility: Windows 11 with PowerShell; the credential store is Windows Credential Manager.
metadata:
  mod: mod:pc
  owner: metis
---

# PC check

1. `GET /api/adapters?refresh=true` runs detection now. For each adapter it reports whether the CLI is on PATH
   (`cli.installed`) and whether it is signed in (`status`). Report names and states only.
2. Docker: `connected` means the engine answers. `error` with "not running" means Docker Desktop is installed but
   stopped; ask the user to start it.
3. Credentials: tokens live only in Windows Credential Manager under targets that start with `4GE:`
   (`4GE:adapter:<slug>`). Never read one into a message, a log or a file. To check one exists, rely on the adapter
   status.
4. Ports: the sandbox needs 17410, 17411 and 17412 free. If one is taken, say which and stop; never kill a process
   you did not start.
