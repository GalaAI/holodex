# Stats demo data

Gala's daily recap reads the user's own activity plus a demo account. It works without a connected Google account.

- Copy `demo-activity.json` to `%LOCALAPPDATA%\Omniwand\demo-activity.json`. Its events replay on today's date, and every one is labelled `demo:` so the recap says "demo account".
- Copy `habits.txt` to `%LOCALAPPDATA%\Omniwand\habits.txt`. Gala asks about any habit that has no matching activity; in this sample, skating.
- Once a dev Google account is connected in Hivemind, a connector source replaces the demo one. The recap and its XP stay the same.

Quests from other agents arrive through AInbox on channel `holodex-quest`. Set `metadata` to `skill`, `kind`, `title`, `briefing`, `rubric`, `difficulty` and `timeLimitSeconds`. Watcher verdicts go out, and can come back, on `holodex-watcher`.
