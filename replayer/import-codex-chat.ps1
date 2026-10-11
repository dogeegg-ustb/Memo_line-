$ErrorActionPreference = 'Stop'
$sourcePath = 'C:/Users/dogeegg/.codex/sessions/2026/10/06/rollout-2026-10-06T21-59-27-01a11183-1d18-7761-9a48-ed47799fc6a7.jsonl'
$sessionId = 'd6946ec4-2a04-4d7e-8e29-d04db890dbef'
$exportDir = 'D:/Memo_Line/Memo_Line/replayer/codex-chat-export'
$targetDir = 'C:/Users/dogeegg/.claude/projects/D--Memo-Line-Memo-Line-replayer'
New-Item -ItemType Directory -Force -Path $exportDir | Out-Null
$records = Get-Content -LiteralPath $sourcePath | ForEach-Object { $_ | ConvertFrom-Json }
$lines = [System.Collections.Generic.List[string]]::new()
$markdown = [System.Collections.Generic.List[string]]::new()
$markdown.Add('# 从 Codex 导入的 Memoline 集成对话')
$parentId = $null
foreach ($record in $records) {
    if ($record.type -ne 'response_item' -or $record.payload.type -ne 'message' -or $record.payload.role -notin @('user','assistant')) { continue }
    $role = $record.payload.role
    $body = ($record.payload.content | Where-Object { $_.type -in @('input_text','output_text','text') } | ForEach-Object { $_.text }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($body) -or $body -match '^<external_codex_apps_open_page>') { continue }
    $messageId = [guid]::NewGuid().ToString()
    if ($role -eq 'user') { $message = @{role='user';content=$body} }
    else { $message = @{role='assistant';type='message';id="msg_import_$messageId";model='imported-codex';content=@(@{type='text';text=$body});stop_reason='end_turn';stop_sequence=$null;usage=@{input_tokens=0;output_tokens=0}} }
    $entry = @{type=$role;parentUuid=$parentId;uuid=$messageId;sessionId=$sessionId;timestamp=$record.timestamp;cwd='D:\Memo_Line\Memo_Line\replayer';version='2.1.286';userType='external';isSidechain=$false;message=$message}
    $lines.Add(($entry | ConvertTo-Json -Depth 20 -Compress))
    $markdown.Add("`n## $role — $($record.timestamp)`n`n$body")
    $parentId = $messageId
}
if ($lines.Count -eq 0) { throw 'No messages exported' }
$lines.Add((@{type='custom-title';customTitle='Memoline 三软件集成（从 Codex 导入）';sessionId=$sessionId} | ConvertTo-Json -Compress))
$exportPath = Join-Path $exportDir "$sessionId.jsonl"
[IO.File]::WriteAllLines($exportPath,$lines,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllLines((Join-Path $exportDir 'conversation.md'),$markdown,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $exportDir 'original-codex.jsonl') -Force
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
$targetPath = Join-Path $targetDir "$sessionId.jsonl"
if (Test-Path -LiteralPath $targetPath) { throw 'Target session already exists' }
Copy-Item -LiteralPath $exportPath -Destination $targetPath
$verified = Get-Content -LiteralPath $targetPath | ForEach-Object { $_ | ConvertFrom-Json }
if ($verified.Count -ne $lines.Count) { throw 'Verification failed' }
Write-Output "Imported $($lines.Count - 1) messages. Session: $sessionId"
Write-Output "Target: $targetPath"
