#Requires AutoHotkey v2.0
#SingleInstance Force
;
; Ark input bridge (moved from GalaAI/ark apps/input-studio/bridge into
; Holodex, Cursor Mod plan step 4).
;
; Turns a mouse button into a spine event.
;
; Neither Logitech surface can call us directly. G HUB's Lua sandbox has no
; network and no file IO, and Options+ is a closed Electron app with no plugin
; drop-in. Both can emit a keystroke, though, so a keystroke is the channel.
; You bind a mouse button to one of the keys below in G HUB or Options+, this
; script sees it and posts a spine event.
;
; Going through keys rather than a vendor SDK is not a workaround, it is the
; better design here: the same bridge works for the Razer Tartarus and anything
; else that can send a key, and it does not break when Logitech changes their
; software.
;
; F13 to F24 are the private channel. No physical keyboard has them and no
; application binds them, so they cannot collide with anything you actually use.
; Ctrl+Alt+F1 to F12 are also carried, for devices whose software will not emit
; the high F keys.

ENDPOINT := "http://127.0.0.1:8842/api/spine/event"
KEYMAP := A_ScriptDir "\keymap.ini"

; --- the Cursor Mod token ----------------------------------------------------
; Holodex runs a binding's shell action only for a caller that sends this PC's
; token, and only while shell actions are turned on in the Cursor Mod tab. The
; token sits in the user's own Holodex folder (the Omniwand folder on installs
; from before the rename) and changes each time shell actions are turned on, so
; it is read on every press. Without it, keys, typing and clicks still run.
TokenPaths := [
    EnvGet("LOCALAPPDATA") "\Holodex\cursor-mod\shell-token",
    EnvGet("LOCALAPPDATA") "\Omniwand\cursor-mod\shell-token",
]

CursorModToken() {
    for path in TokenPaths {
        if FileExist(path) {
            try return Trim(FileRead(path), " `t`r`n")
        }
    }
    return ""
}

; --- key to trigger name -----------------------------------------------------
; A trigger called "logitech/forward" is worth reading in a binding list;
; "F13" is not. The map is a plain ini so it can be edited without a parser,
; and an unmapped key still fires under its own name rather than being dropped.
names := Map()
if FileExist(KEYMAP) {
    for line in StrSplit(FileRead(KEYMAP), "`n", "`r") {
        line := Trim(line)
        if (line = "" || SubStr(line, 1, 1) = ";" || SubStr(line, 1, 1) = "[")
            continue
        eq := InStr(line, "=")
        if eq
            names[Trim(SubStr(line, 1, eq - 1))] := Trim(SubStr(line, eq + 1))
    }
}

TriggerFor(key) => names.Has(key) ? names[key] : key

; --- posting -----------------------------------------------------------------
; Fired on a timer with a negative delay so the hotkey returns immediately.
; A blocking HTTP call inside a hotkey makes the button feel like it lagged,
; which reads as the binding being broken even when it worked.
Fire(key) {
    SetTimer(() => Post(key), -1)
}

Post(key) {
    app := ""
    try app := WinGetProcessName("A")            ; drives per-app profiles
    app := RegExReplace(app, "i)\.exe$", "")

    body := '{"trigger":{"kind":"device","match":"' TriggerFor(key) '"}'
    if (app != "")
        body .= ',"profile":"' app '"'
    body .= "}"

    try {
        http := ComObject("WinHttp.WinHttpRequest.5.1")
        http.SetTimeouts(1000, 1000, 2000, 15000)
        http.Open("POST", ENDPOINT, false)
        http.SetRequestHeader("Content-Type", "application/json")
        token := CursorModToken()
        if (token != "")
            http.SetRequestHeader("X-Cursor-Mod-Token", token)
        ; No Origin header on purpose. Holodex refuses cross-origin POSTs
        ; because the spine can run shell commands, and a request with no
        ; Origin at all is how a non-browser caller like this one identifies.
        http.Send(body)
        Log(key " -> " TriggerFor(key) " [" app "] " http.Status " " http.ResponseText)
    } catch as err {
        Log(key " -> POST failed: " err.Message)
    }
}

Log(text) {
    try FileAppend(FormatTime(, "HH:mm:ss") "  " text "`n", A_ScriptDir "\bridge.log")
}

; --- hotkeys -----------------------------------------------------------------
Loop 12 {
    k := "F" (A_Index + 12)                       ; F13..F24, the private channel
    Hotkey("*" k, (*) => Fire(ThisHotkeyKey()))
}
Loop 12 {
    k := "F" A_Index                              ; Ctrl+Alt+F1..F12 as a fallback
    Hotkey("^!" k, (*) => Fire(ThisHotkeyKey()))
}

; The handlers are generated in a loop, so each one has to ask which hotkey
; actually fired rather than capturing the loop variable.
ThisHotkeyKey() {
    k := RegExReplace(A_ThisHotkey, "^[\*\^\!\+#]+", "")
    if InStr(A_ThisHotkey, "^") && InStr(A_ThisHotkey, "!")
        return "Ctrl+Alt+" k
    return k
}

TraySetIcon("shell32.dll", 78)
A_IconTip := "Ark input bridge  ->  " ENDPOINT
Log("bridge started, " names.Count " named triggers")
