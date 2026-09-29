#Requires AutoHotkey v2.0
#SingleInstance Force
SendMode "Input"
SetMouseDelay -1

enabled := true
saveSettleMs := 150  ; 发送 Ctrl+S 后等待 CSP 处理；需要时可调大

F8:: {
    global enabled
    enabled := !enabled
    ToolTip(enabled ? "CSP 左键前保存：已启用" : "CSP 左键前保存：已停用")
    SetTimer(() => ToolTip(), -1000)
}

#HotIf enabled && WinActive("ahk_exe CLIPStudioPaint.exe")
$*LButton:: {
    global saveSettleMs
    Send "^s"
    Sleep saveSettleMs
    Send "{LButton down}"
    KeyWait "LButton", "P"
    Send "{LButton up}"
}
#HotIf
