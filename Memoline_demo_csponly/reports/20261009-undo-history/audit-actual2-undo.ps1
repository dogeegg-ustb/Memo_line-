$ErrorActionPreference = 'Stop'
$undoAuditRoot = $PSScriptRoot
$undoAuditCache = [IO.File]::ReadAllText((Join-Path $undoAuditRoot 'latest-audit-cache.txt'))
$undoAuditSource = 'D:\Memo_Line\Memo_Line\Memoline_demo_csponly\publish\win-x64\recordings\记艺线测试2.memoline'
$undoAuditIndex = Get-Content -LiteralPath (Join-Path $undoAuditCache 'index.json') -Raw | ConvertFrom-Json -Depth 100
$undoAuditRows = @(Get-Content -LiteralPath (Join-Path $undoAuditCache 'controls.json') -Raw | ConvertFrom-Json -Depth 100)
$undoAuditKinds = Get-Content -LiteralPath (Join-Path $undoAuditCache 'native-kinds.json') -Raw | ConvertFrom-Json
$undoHistorySlots = @(Get-Content -LiteralPath (Join-Path $undoAuditCache 'history-slots.json') -Raw | ConvertFrom-Json -Depth 100)

# Packets were decoded from the sealed aggregation stream. Keep a small reproducible
# projection rather than retaining coverage segments and large recognizer snapshots twice.
$undoPacketCompactPath = Join-Path $undoAuditCache 'packets-mini.json'
if (Test-Path -LiteralPath $undoPacketCompactPath) {
    $undoAuditPackets = @(Get-Content -LiteralPath $undoPacketCompactPath -Raw | ConvertFrom-Json -Depth 30)
} else {
    $undoAuditPacketFullPath = Join-Path $undoAuditCache 'packets.json'
    $undoAuditPacketFull = @(Get-Content -LiteralPath $undoAuditPacketFullPath -Raw | ConvertFrom-Json -Depth 100)
    $undoAuditPackets = @($undoAuditPacketFull | ForEach-Object {
        [pscustomobject]@{
            id = $_.id; fromTicks = $_.fromTicks; toTicks = $_.toTicks; triggerTicks = $_.triggerTicks
            boundaryKind = $_.boundaryKind; status = $_.status; reason = $_.reason
            layerUuid = $_.manifest.now.layer.uuid; afterLayerUuid = $_.manifest.after.layer.uuid
            images = @($_.manifest.images | ForEach-Object { [pscustomobject]@{ id=$_.id; labelIds=$_.labelIds } })
            labels = @($_.manifest.labels | ForEach-Object {
                [pscustomobject]@{ id=$_.id; operationId=$_.operationId; source=$_.source
                    fromTicks=$_.fromTicks; toTicks=$_.toTicks; imageIds=$_.imageIds
                    penDownRegion=$_.penDownLocation.region }
            })
            pointers = @($_.eventPointers | ForEach-Object {
                [pscustomobject]@{ appendId=$_.appendId; eventId=$_.eventId; kind=$_.kind; ticks=$_.ticks; operationId=$_.operationId }
            })
        }
    })
    [IO.File]::WriteAllText($undoPacketCompactPath, ($undoAuditPackets | ConvertTo-Json -Depth 30))
    $undoAuditPackets = @($undoAuditPackets)
    $undoAuditPacketFull = $null
    # This removes only this audit's redundant generated JSON, after checking its exact path.
    $undoAuditExpectedPacket = [IO.Path]::GetFullPath((Join-Path $undoAuditCache 'packets.json'))
    if ([IO.Path]::GetFullPath($undoAuditPacketFullPath) -ne $undoAuditExpectedPacket -or
        -not $undoAuditExpectedPacket.StartsWith([IO.Path]::GetFullPath($undoAuditRoot) + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'Audit output path escaped report directory.' }
    Remove-Item -LiteralPath $undoAuditExpectedPacket
}

$undoNativeEnds = @{}
foreach ($undoEnd in ($undoAuditRows | Where-Object kind -eq penEnd)) {
    $undoNativeEnds[[string]$undoEnd.operationId] = $undoEnd
}
$undoNativePens = [Collections.Generic.List[object]]::new()
foreach ($undoBegin in ($undoAuditRows | Where-Object kind -eq penBegin)) {
    $undoEnd = $undoNativeEnds[[string]$undoBegin.operationId]
    $undoBrushFrame = $undoAuditRows | Where-Object {
        $_.kind -eq 'coreStateUpdated' -and $_.data.module -eq 'brushState' -and
        $_.appendId -lt $undoBegin.appendId -and $_.data.status -in @('changed','unchanged')
    } | Select-Object -Last 1
    $undoLayerFrame = $undoAuditRows | Where-Object {
        $_.kind -eq 'coreStateUpdated' -and $_.data.module -eq 'currentLayerState' -and
        $_.appendId -lt $undoBegin.appendId -and $_.data.status -in @('changed','unchanged')
    } | Select-Object -Last 1
    $undoClipFrame = $undoAuditRows | Where-Object {
        $_.kind -eq 'coreStateUpdated' -and $_.data.module -eq 'clipState' -and
        $_.appendId -lt $undoBegin.appendId -and $_.data.status -in @('changed','unchanged')
    } | Select-Object -Last 1
    $undoBrushSlot = $undoHistorySlots | Where-Object {$_.Module -eq 'brushState' -and $_.Anchor -lt $undoBegin.eventId} | Select-Object -Last 1
    $undoLayerSlot = $undoHistorySlots | Where-Object {$_.Module -eq 'currentLayerState' -and $_.Anchor -lt $undoBegin.eventId} | Select-Object -Last 1
    $undoLayerName = [string]$undoLayerSlot.Data.state
    $undoLayerCandidate = @($undoClipFrame.data.state.layers | Where-Object {
        ($_.name -replace '\s','') -eq ($undoLayerName -replace '\s','')
    })
    $undoPacketsForPen = @($undoAuditPackets | Where-Object { $_.labels.operationId -contains $undoBegin.operationId } |
        ForEach-Object { [pscustomobject]@{id=$_.id; triggerTicks=$_.triggerTicks; layerUuid=$_.layerUuid
            labels=@($_.labels | Where-Object operationId -eq $undoBegin.operationId)} })
    $undoIsPan = @($undoBegin.data.heldKeys) -contains 32
    $undoNativePens.Add([pscustomobject]@{
        key = [pscustomobject]@{sessionId=$undoAuditIndex.sessionId; beginAppendId=$undoBegin.appendId}
        operationId=$undoBegin.operationId; beginEventId=$undoBegin.eventId; beginTicks=$undoBegin.ticks
        endAppendId=$undoEnd.appendId; endEventId=$undoEnd.eventId; endTicks=$undoEnd.ticks
        complete=($null -ne $undoEnd -and $undoEnd.appendId -gt $undoBegin.appendId)
        location=$undoBegin.data.penDownLocation.region; heldKeys=$undoBegin.data.heldKeys
        interpretation=$(if($undoIsPan){'SpaceHeldCanvasNavigation'}else{'CanvasBrushContactCandidate'})
        brush=$undoBrushSlot.Data.state.name
        brushProperties=$undoBrushSlot.Data.state.properties
        brushHistoryAnchor=$undoBrushSlot.Anchor; brushHistoryOrder=$undoBrushSlot.Order
        brushLatestPublishedFrameAppend=$undoBrushFrame.appendId; brushCapturedTicks=$undoBrushSlot.Data.evidence.capturedTicks
        brushCausalAmbiguous=$undoBrushSlot.Data.evidence.causalAmbiguous
        brushHistoricalStateValid=($undoBrushSlot.Data.status -in @('changed','unchanged') -and -not $undoBrushSlot.Data.evidence.causalAmbiguous)
        layerName=$undoLayerName
        layerUuid=$(if($undoLayerCandidate.Count -eq 1){$undoLayerCandidate[0].uuid}else{$null})
        layerHistoryAnchor=$undoLayerSlot.Anchor; layerHistoryOrder=$undoLayerSlot.Order
        layerLatestPublishedFrameAppend=$undoLayerFrame.appendId; clipSourceAppend=$undoClipFrame.appendId
        packets=$undoPacketsForPen
    })
}

$undoAuditKeys = @{}
foreach ($undoKey in ($undoAuditRows | Where-Object kind -eq keyInput)) { $undoAuditKeys[[string]$undoKey.eventId] = $undoKey }
$undoAuditCommands = @($undoAuditRows | Where-Object {
    $_.kind -eq 'shortcutResolved' -and @($_.data.matches.command | Where-Object { $_ -in @('undo','redo') }).Count -gt 0
})
$undoWritePens = @($undoNativePens | Where-Object {
    $_.complete -and $_.location -eq 'canvasViewport' -and $_.interpretation -ne 'SpaceHeldCanvasNavigation'
})
$undoStack = [Collections.Generic.List[object]]::new()
$undoRedoStack = [Collections.Generic.List[object]]::new()
$undoTransitions = [Collections.Generic.List[object]]::new()
$undoCommandRows = [Collections.Generic.List[object]]::new()
$undoOrdered = @($undoWritePens | ForEach-Object { [pscustomobject]@{append=$_.endAppendId;kind='write';pen=$_} })
$undoOrdered += @($undoAuditCommands | ForEach-Object {
    $undoResolved=$_.relatedEventIds | ForEach-Object { $undoAuditKeys[[string]$_] } | Select-Object -First 1
    [pscustomobject]@{append=$undoResolved.appendId;kind='command';command=$_;input=$undoResolved}
})
foreach ($undoEntry in ($undoOrdered | Sort-Object append)) {
    if ($undoEntry.kind -eq 'write') {
        if ($undoRedoStack.Count -gt 0) {
            $undoTransitions.Add([pscustomobject]@{kind='newCanvasContactClearsRedo';appendId=$undoEntry.append
                operationId=$undoEntry.pen.operationId;beginAppendId=$undoEntry.pen.key.beginAppendId
                clearedOperations=@($undoRedoStack | ForEach-Object operationId)})
            $undoRedoStack.Clear()
        }
        $undoStack.Add($undoEntry.pen)
        continue
    }
    $undoResolvedCommand = $undoEntry.command.data.matches.command | Where-Object {$_ -in @('undo','redo')} | Select-Object -First 1
    $undoTarget = $null
    if ($undoResolvedCommand -eq 'undo' -and $undoStack.Count -gt 0) {
        $undoTarget = $undoStack[$undoStack.Count-1]
        $undoStack.RemoveAt($undoStack.Count-1)
        $undoRedoStack.Add($undoTarget)
    } elseif ($undoResolvedCommand -eq 'redo' -and $undoRedoStack.Count -gt 0) {
        $undoTarget = $undoRedoStack[$undoRedoStack.Count-1]
        $undoRedoStack.RemoveAt($undoRedoStack.Count-1)
        $undoStack.Add($undoTarget)
    }
    $undoPreviousPens = @($undoNativePens | Where-Object {$_.endAppendId -lt $undoEntry.append} | Select-Object -Last 10)
    $undoAfterPens = @($undoNativePens | Where-Object {$_.key.beginAppendId -gt $undoEntry.append} | Select-Object -First 4)
    $undoCommandRows.Add([pscustomobject]@{
        command=$undoResolvedCommand; shortcutAppendId=$undoEntry.command.appendId
        shortcutTicks=$undoEntry.command.ticks; relatedEventIds=$undoEntry.command.relatedEventIds
        input=$undoEntry.input
        inferredTarget=$undoTarget
        mostRecentCompleteContacts=$undoPreviousPens
        followingContacts=$undoAfterPens
        inputPackets=@($undoAuditPackets | Where-Object {$_.pointers.eventId -contains $undoEntry.input.eventId} | ForEach-Object id)
    })
}

$undoSummary=[ordered]@{
    source=$undoAuditSource; sourceHash=(Get-FileHash -LiteralPath $undoAuditSource -Algorithm SHA256).Hash
    sessionId=$undoAuditIndex.sessionId; frequency=$undoAuditIndex.frequency
    nativeSizeAndHashVerified=((Get-Item -LiteralPath (Join-Path $undoAuditCache 'recording.memoline')).Length -eq $undoAuditIndex.mechanical.size -and
        (Get-FileHash -LiteralPath (Join-Path $undoAuditCache 'recording.memoline')).Hash.ToLowerInvariant() -eq $undoAuditIndex.mechanical.sha256)
    aggregationSizeAndHashVerified=((Get-Item -LiteralPath (Join-Path $undoAuditCache 'events.jsonl')).Length -eq $undoAuditIndex.aggregation.size -and
        (Get-FileHash -LiteralPath (Join-Path $undoAuditCache 'events.jsonl')).Hash.ToLowerInvariant() -eq $undoAuditIndex.aggregation.sha256)
    nativeFramesCrcValidated=$true
    frameCounts=$undoAuditKinds
    penBeginCount=$undoNativePens.Count; completePenContacts=@($undoNativePens | Where-Object complete).Count
    canvasBrushContactCandidates=$undoWritePens.Count
    spaceNavigationContacts=@($undoNativePens | Where-Object interpretation -eq SpaceHeldCanvasNavigation)
    undoCount=@($undoCommandRows | Where-Object command -eq undo).Count
    redoCount=@($undoCommandRows | Where-Object command -eq redo).Count
    undoCommands=@($undoCommandRows)
    redoTransitions=@($undoTransitions)
    finalContactStackCount=$undoStack.Count; finalRedoCount=$undoRedoStack.Count
    allCompletePenContacts=@($undoNativePens)
    packetSummary=@($undoAuditPackets | Select-Object id,fromTicks,toTicks,triggerTicks,boundaryKind,status,reason,layerUuid)
    otherInputSummary=@($undoAuditRows | Where-Object kind -eq keyInput | Select-Object appendId,eventId,ticks,data)
    otherRecordedPointerKinds=@($undoAuditKinds.PSObject.Properties | Where-Object Name -Like 'mouse*' | Select-Object Name,Value)
    inferenceScope='Undo target stack is inferred from complete observed canvas brush/eraser contacts across all packet boundaries. Space-held contacts are navigation. Brush/layer causality is resolved by production PacketReplay.StateHistory.Resolve from original native statePackageReserved/statePackageResult/coreStateUpdated; original slot anchors, order, and evidence are retained. This audit does not claim a live CSP undo-stack readback.'
    inputUnchanged=((Get-FileHash -LiteralPath $undoAuditSource -Algorithm SHA256).Hash -eq '114E9DC5F5F5E3810F2B2F53AC0E3082644BE746EDF5BEADA8E990229D2F038A')
}
[IO.File]::WriteAllText((Join-Path $undoAuditRoot 'actual2-undo-summary.json'), ($undoSummary | ConvertTo-Json -Depth 50))
$undoCommandRows | ForEach-Object { [pscustomobject]@{
    command=$_.command; inputAppend=$_.input.appendId; eventId=$_.input.eventId
    shortcutAppend=$_.shortcutAppendId; operation=$_.inferredTarget.operationId
    targetBeginAppend=$_.inferredTarget.key.beginAppendId; targetEndAppend=$_.inferredTarget.endAppendId
    tool=$_.inferredTarget.brush; layer=$_.inferredTarget.layerUuid
    packets=$_.inferredTarget.packets.id -join ','
} } | ConvertTo-Json -Depth 4
$undoTransitions | ConvertTo-Json -Depth 5
Write-Output "Report written: $(Join-Path $undoAuditRoot 'actual2-undo-summary.json')"
