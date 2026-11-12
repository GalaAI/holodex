# Ark input bridge

Turns a mouse or keypad button into a Cursor Mod event. Bind a button to F13 to
F24 (or Ctrl+Alt+F1 to F12) in G HUB, Options+ or Razer Synapse; the script
posts `{"trigger":{"kind":"device","match":"<name>"}}` to Holodex on
`127.0.0.1:8842/api/spine/event`, with the foreground app as the profile.
`keymap.ini` names each key ("F13 = logitech/forward").

Run it with AutoHotkey v2. It sends this PC's Cursor Mod token from
`%LOCALAPPDATA%\Holodex\cursor-mod\shell-token` when one exists, so a binding's
shell action runs once shell actions are turned on in the Cursor Mod tab.
Each press is logged to `bridge.log` beside the script.

Moved from GalaAI/ark `apps/input-studio/bridge` (Cursor Mod plan step 4).
