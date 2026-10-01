# 记录器整合检查点

整合版的主程序源码位于 `src/BehaviorRecognizer`，当前 Windows 发行版位于 `publish/win-x64`。程序文件名仍为 `BehaviorRecognizer.exe`。

共享的状态核心、辅助进程与文件解析器分别位于同级的 `../recognizer_core`、`../recorder_integration`、`../MemolineToJson`。构建及发布使用 `../recorder_integration/publish.ps1`。

当前阶段：记录器整合完成，正在进行转换核心的 bug 修复。

原有 `../behavior_recognizer` 将从整合前提交 `3ebee5b` 原样恢复，保留早期 `.strokebin` 数位笔记录器；后续整合开发在本目录进行。

本机既有录制会话随发行版一起搬移；录制内容及 spool 继续由 Git 忽略。
