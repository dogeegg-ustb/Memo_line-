#Requires AutoHotkey v2.0
#SingleInstance Force
SendMode "Input"
SetMouseDelay -1

; Recorder owns the interception and ordered replay. This mode only performs
; Ctrl+S on its dedicated background process, without waiting for file I/O.
recorderWorker := A_Args.Length && A_Args[1] = "--recorder-worker"
enabled := false
if recorderWorker {
    input := FileOpen("*", "r", "UTF-8")
    FileAppend("ready`n", "*", "UTF-8")
    while !input.AtEOF {
        line := Trim(input.ReadLine(), "`r`n ")
        if !line
            continue
        parts := StrSplit(line, " ")
        target := Integer(parts[1])
        if !WinActive("ahk_id " target) || WinGetProcessName("ahk_id " target) != "CLIPStudioPaint.exe" {
            FileAppend("foreground-mismatch`n", "*", "UTF-8")
            continue
        }
        if !RecorderSaveInput() {
            FileAppend("save-input-failed`n", "*", "UTF-8")
            continue
        }
        ; SendInput has returned; this is dispatch acknowledgement, not CSP/file completion.
        FileAppend("save-input-sent`n", "*", "UTF-8")
    }
    ExitApp
}

enabled := true

#HotIf !recorderWorker
F8:: {
    global enabled
    enabled := !enabled
    ToolTip(enabled ? "CSP 左键前保存：已启用" : "CSP 左键前保存：已停用")
    SetTimer(() => ToolTip(), -1000)
}

#HotIf !recorderWorker && enabled && WinActive("ahk_exe CLIPStudioPaint.exe")
$*LButton:: {
    Send "^s"
    Send "{LButton down}"
    KeyWait "LButton"
    Send "{LButton up}"
}
#HotIf

; Tag only Recorder-owned input, so tablet-driver keyboard mappings remain observable.
RecorderSaveInput() {
    held := []
    for item in [["LControl", 0xA2], ["RControl", 0xA3], ["LShift", 0xA0], ["RShift", 0xA1],
                 ["LAlt", 0xA4], ["RAlt", 0xA5], ["LWin", 0x5B], ["RWin", 0x5C]] {
        if GetKeyState(item[1])
            held.Push(item[2])
    }
    keys := []
    for vk in held
        keys.Push([vk, 2])
    keys.Push([0xA2, 0], [0x53, 0], [0x53, 2], [0xA2, 2])
    for vk in held
        keys.Push([vk, 0])
    size := A_PtrSize = 8 ? 40 : 28
    offset := A_PtrSize = 8 ? 8 : 4
    extra := A_PtrSize = 8 ? 16 : 12
    inputs := Buffer(size * keys.Length, 0)
    for i, key in keys {
        inputOffset := (i - 1) * size
        extended := key[1] = 0xA3 || key[1] = 0xA5 || key[1] = 0x5B || key[1] = 0x5C
        NumPut("UInt", 1, inputs, inputOffset)
        NumPut("UShort", key[1], inputs, inputOffset + offset)
        NumPut("UInt", key[2] | (extended ? 1 : 0), inputs, inputOffset + offset + 4)
        NumPut("UPtr", 0x4D4C5243, inputs, inputOffset + offset + extra)
    }
    return DllCall("user32\SendInput", "UInt", keys.Length, "Ptr", inputs.Ptr, "Int", size, "UInt") = keys.Length
}
