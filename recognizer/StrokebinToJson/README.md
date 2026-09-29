# StrokebinToJson

将 BehaviorRecognizer 生成的 `.strokebin`（STRO v1）笔迹记录译码为结构化 JSON。二进制解析和 JSON 字段定义复用 `behavior_recognizer` 项目中的 `StrokeBinaryReader` 与 `StrokeJsonExporter`。

## 使用

在工作区根目录运行：

```powershell
.\StrokebinToJson.bat .\behavior_recognizer\publish\win-x64\procedure\stroke\20260925_063421.strokebin
```

默认输出到输入文件旁的同名 `.json` 文件。也可以指定输出路径：

```powershell
.\StrokebinToJson.bat .\input.strokebin .\output\recording.json
```

直接使用 .NET CLI：

```powershell
dotnet run --project .\StrokebinToJson\StrokebinToJson.csproj -c Release -- .\input.strokebin
```
