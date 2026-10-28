using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class PreciseUndoChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var layer = new LayerKey("precise-undo", 7, "ink-uuid");
        var other = new LayerKey(layer.SessionId, layer.Generation, "other-uuid");
        var region = Mask(0);
        var candidates = Enumerable.Range(1, 60).Select(n => Stroke(layer, (ulong)n, Mask(0, 1))).ToArray();
        var firstAdded = Stroke(layer, 61, Mask(1, 2));
        var secondAdded = Stroke(layer, 62, Mask(2, 3));
        var thirdAdded = Stroke(layer, 63, Mask(3, 4));
        var disjoint = Stroke(layer, 64, Mask(7));
        var foreign = Stroke(other, 100, Mask(0, 4));
        var candidateImage = Image(layer, "base", "candidate-image", Mask(0, 1), candidates);
        var p0 = Packet(layer, "base", 10, candidateImage);
        var p1 = Packet(layer, "later-1", 20, Image(layer, "later-1", "i1", Mask(2), [firstAdded]));
        var p2 = Packet(layer, "later-2", 30, Image(layer, "later-2", "i2", Mask(3), [secondAdded]));
        var p3 = Packet(layer, "later-3", 40, Image(layer, "later-3", "i3", Mask(4), [thirdAdded]));
        var p4 = Packet(other, "foreign", 1000, Image(other, "foreign", "i4", Mask(0, 4), [foreign]));
        var all = candidates.Concat([firstAdded, secondAdded, thirdAdded, disjoint, foreign]).Reverse().ToArray();
        var index = Index([p3, p1, p4, p0, p2], all);
        var selection = PreciseUndoSelector.Select(index, layer, region);
        Check(selection.IsValid, "Scenario 17: selecting sixty eligible candidates must succeed.");
        Check(selection.CandidateCount == 60 && selection.BaseCount == 50 &&
              selection.Base.Select(s => s.BeginEventId).SequenceEqual(Enumerable.Range(11, 50).Select(n => (ulong)n)),
            "Scenario 17: take the latest fifty candidates, not the oldest fifty or appendId order.");
        Check(selection.Selected.Select(s => s.BeginEventId).SequenceEqual(Enumerable.Range(1, 60).Select(n => (ulong)n)),
            "The image the selection inked is replayed whole: its older strokes join, in original penBegin eventId order.");
        Check(selection.ClosureCount == 10 && selection.CountDisplay == "50 + 10" &&
              selection.Reasons.All(r => r.Image == candidateImage.Key && !string.IsNullOrWhiteSpace(r.Message)),
            "The closure names the replayed image each added stroke is in, with an explanation for the panel.");
        Check(!selection.Selected.Any(s => s.Key == firstAdded.Key || s.Key == disjoint.Key || s.Key == foreign.Key),
            "Later images apart from the replayed one, disconnected strokes and another layer stay pasted as recorded.");
        Check(selection.Images.SequenceEqual(new[] { candidateImage.Key }),
            "Only the inked image is replayed; another layer's masks do not matter.");
        Check(region.Count == 1 && candidateImage.Mask.Count == 2 && firstAdded.WriteMask.Count == 2,
            "Selection must not mutate the input region, image masks or write footprints.");

        var few = Enumerable.Range(1, 7).Select(n => Stroke(layer, (ulong)n, Mask(0))).ToArray();
        var fewImage = Image(layer, "few", "few-image", Mask(0), few);
        var fewSelection = PreciseUndoSelector.Select(Index([Packet(layer, "few", 10, fewImage)], few.Reverse().ToArray()), layer, region);
        Check(fewSelection.IsValid && fewSelection.BaseCount == 7 && fewSelection.CandidateCount == 7,
            "Scenario 18: fewer than fifty candidates uses the actual number.");
        Check(fewSelection.Selected.Select(s => s.BeginEventId).SequenceEqual(Enumerable.Range(1, 7).Select(n => (ulong)n)) &&
              fewSelection.ClosureCount == 0 && fewSelection.CountDisplay == "7",
            "Scenario 18: original ordering and displayed count must not imply fifty strokes.");

        // memolinetest13 (2026-10-26): the glasses and hair are one image, the mouth and the nose dot two more. A region on
        // an eye replays the big image; the mouth and the dot are pasted as recorded, never drawn again.
        var eye = Stroke(layer, 1, Mask(0, 1)); var hair = Stroke(layer, 2, Mask(1));
        var mouth = Stroke(layer, 3, Mask(4)); var dot = Stroke(layer, 4, Mask(6));
        // The watcher's padded footprint of the dot reaches the big image: linked to both, it inked only the dot.
        dot = dot with { WriteMask = Mask(1, 6), InkMask = Mask(6) };
        var face = Image(layer, "face", "glasses-hair", Mask(0, 1), [eye, hair, dot]);
        var mouthImage = Image(layer, "face", "mouth", Mask(4), [mouth]);
        var dotImage = Image(layer, "face", "dot", Mask(6), [dot]);
        var faceIndex = Index([Packet(layer, "face", 10, face, mouthImage, dotImage)], [eye, hair, mouth, dot]);
        var faceSelection = PreciseUndoSelector.Select(faceIndex, layer, Mask(0));
        Check(faceSelection.IsValid && faceSelection.Base.Single().Key == eye.Key &&
              faceSelection.Closure.Select(s => s.Key).SequenceEqual([hair.Key]) && faceSelection.Images.SequenceEqual([face.Key]),
            "Images the selection did not ink are pasted; a stroke linked to the replayed image only by its padded footprint stays out.");
        var padded = Stroke(layer, 5, Mask(0, 4)) with { InkMask = Mask(4) };
        var paddedIndex = Index([Packet(layer, "padded", 10, Image(layer, "padded", "near", Mask(4), [padded]),
            Image(layer, "padded", "far", Mask(0), [eye]))], [padded, eye]);
        Check(PreciseUndoSelector.Select(paddedIndex, layer, Mask(0)) is { IsValid: true } byInk &&
              byInk.Selected.Select(s => s.Key).SequenceEqual([eye.Key]),
            "The region selects by ink: the write footprint's safety padding does not make a stroke a candidate.");

        // One contact is replayed whole, so every image it inked is replayed with it.
        var bridge = Stroke(layer, 1, Mask(0, 3)); var leftOnly = Stroke(layer, 2, Mask(0)); var rightOnly = Stroke(layer, 3, Mask(3));
        var bridgeIndex = Index([Packet(layer, "bridge", 10, Image(layer, "bridge", "left", Mask(0), [bridge, leftOnly]),
            Image(layer, "bridge", "right", Mask(3), [bridge, rightOnly]))], [bridge, leftOnly, rightOnly]);
        Check(PreciseUndoSelector.Select(bridgeIndex, layer, Mask(3)) is { IsValid: true } shared &&
              shared.Images.Count == 2 && shared.Selected.Count == 3,
            "A replayed stroke that inked two images brings the other image and its strokes along.");

        // A later image over a replayed one holds the rebuilt ink beneath: it is replayed too, the earlier strokes it covers
        // are pasted no more. An earlier image under it is the pixels beneath and stays pasted.
        var under = Stroke(layer, 1, Mask(2)); var early = Stroke(layer, 2, Mask(0, 1)); var over = Stroke(layer, 3, Mask(1, 2));
        var overIndex = Index([Packet(layer, "early", 10, Image(layer, "early", "under", Mask(2), [under]), Image(layer, "early", "early", Mask(0, 1), [early])),
            Packet(layer, "late", 20, Image(layer, "late", "over", Mask(1, 2), [over]))], [under, early, over]);
        var overSelection = PreciseUndoSelector.Select(overIndex, layer, Mask(0));
        Check(overSelection.IsValid && overSelection.Base.Single().Key == early.Key &&
              overSelection.Closure.Select(s => s.Key).SequenceEqual([over.Key]) && overSelection.Images.Count == 2,
            "A later image over a replayed image is replayed; an earlier image beside it is not.");
        Check(PreciseUndoSelector.Select(overIndex, layer, Mask(1, 2)) is { IsValid: true } latest &&
              latest.Base.Select(s => s.Key).SequenceEqual([under.Key, early.Key, over.Key]),
            "Strokes of every image the region inked are all selected, in order.");

        var notWritingRegion = Stroke(layer, 3, Mask(5));
        var eligible = Stroke(layer, 4, Mask(0));
        var footprints = Image(layer, "footprints", "f", Mask(0), [notWritingRegion, eligible]);
        var writeSelected = PreciseUndoSelector.Select(Index([Packet(layer, "footprints", 10, footprints)], [notWritingRegion, eligible]), layer, region);
        Check(writeSelected.Base.Select(s => s.Key).SequenceEqual(new[] { eligible.Key }),
            "Candidate hit testing uses the stroke's own ink, not the dirty image change mask.");

        var croppedStroke = Stroke(layer, 5, CroppedMask(5));
        var croppedImage = Image(layer, "crop", "crop-image", CroppedMask(5), [croppedStroke]);
        var cropped = PreciseUndoSelector.Select(Index([Packet(layer, "crop", 10, croppedImage)], [croppedStroke]), layer, Mask(5));
        Check(cropped.IsValid && cropped.Selected.Single().Key == croppedStroke.Key,
            "Cropped image and ink masks must intersect a full-canvas selection by canvas coordinates.");

        var duplicateStroke = Stroke(layer, 6, Mask(0, 1));
        var d0 = Image(layer, "d0", "d0-image", Mask(0), [duplicateStroke]);
        var d1 = Image(layer, "d1", "d1-image", Mask(1), [duplicateStroke]);
        var deduplicated = PreciseUndoSelector.Select(Index([Packet(layer, "d1", 20, d1), Packet(layer, "d0", 10, d0)], [duplicateStroke]), layer, Mask(0, 1));
        Check(deduplicated.IsValid && deduplicated.Images.Count == 2 && deduplicated.BaseCount == 1 && deduplicated.Selected.Count == 1,
            "One complete contact linked to multiple images is selected and replayed only once.");

        // A stroke that cannot be replayed keeps its recorded pixels where nothing replayed can write; where something
        // can, those pixels would be lost, so the undo stops.
        var good = Stroke(layer, 10, Mask(0));
        var apart = Stroke(layer, 9, Mask(3)) with { Eligible = false, IneligibleReason = "Unconfirmed brush" };
        var apartIndex = Index([Packet(layer, "eligibility", 10, Image(layer, "eligibility", "good-image", Mask(0, 3), [apart, good]))], [apart, good]);
        Check(PreciseUndoSelector.Select(apartIndex, layer, region) is { IsValid: true } kept &&
              kept.Selected.Single().Key == good.Key,
            "An unreplayable stroke away from every replayed stroke stays as recorded pixels and does not block.");
        var laterBad = Stroke(layer, 11, Mask(0)) with { Eligible = false, IneligibleReason = "Unconfirmed brush" };
        var blocked = PreciseUndoSelector.Select(Index([Packet(layer, "eligibility", 10, Image(layer, "eligibility", "bad-image", Mask(0), [good, laterBad]))],
            [good, laterBad]), layer, region);
        Check(!blocked.IsValid && blocked.Issues.Any(i => i.Code == PlanIssueCode.UnreplayableEventInMatrix && i.Stroke == laterBad.Key),
            "An unreplayable stroke under the replayed strokes' reach must block precise undo instead of being silently lost.");
        Check(blocked.Base.Single().Key == good.Key && !blocked.Selected.Any(s => s.Key == laterBad.Key),
            "Unreplayable strokes are never selected or replayed.");

        foreach (LayerKey? unconfirmed in new LayerKey?[]
        { null, layer with { Uuid = "" }, layer with { SessionId = "different-session" }, layer with { Generation = 8 }, layer with { Uuid = "missing-uuid" } })
        {
            var rejected = PreciseUndoSelector.Select(index, unconfirmed, region);
            Check(!rejected.IsValid && rejected.Selected.Count == 0 &&
                  rejected.Issues.Any(i => i.Code == PlanIssueCode.LayerIdentityUnconfirmed),
                "An unconfirmed or nonmatching current layer cannot fall back to a name or a UUID in another session/generation.");
        }
        var empty = PreciseUndoSelector.Select(index, layer, new PixelMask(8, 1));
        Check(!empty.IsValid && empty.Issues.Any(i => i.Code == PlanIssueCode.NoSelection),
            "An empty region must stop before selecting events.");
        var noCandidates = PreciseUndoSelector.Select(index, layer, Mask(6));
        Check(!noCandidates.IsValid && noCandidates.BaseCount == 0 && noCandidates.Issues.Any(i => i.Code == PlanIssueCode.NoSelection),
            "A region with no eligible ink reports no selectable strokes.");
        var baseline = Packet(layer, "baseline", 100, Image(layer, "baseline", "baseline-image", Mask(0), [])) with { IsRecordingBaseline = true };
        var baselineIgnored = PreciseUndoSelector.Select(Index([p0, baseline], candidates), layer, region);
        Check(baselineIgnored.IsValid && baselineIgnored.CandidateCount == 60,
            "A recording baseline is not a later dirty image and must not occlude eligible candidates.");

        // The request deletes only what the region selected; the closure is replayed unchanged before it.
        var split = faceSelection.Request(Mask(0));
        Check(split.Changes.Keys.SequenceEqual([eye.Key]) && split.Changes.Values.All(c => c.Delete) && split.Closure.SetEquals([hair.Key]),
            "A precise-undo request deletes the selection only and carries the closure separately (it stays the user's events).");
        // Recorded pixels: the replayed image's own, then the pasted images'. Before the packet the layer was blank.
        var colored = faceIndex with { Images = faceIndex.Images.ToDictionary(kv => kv.Key, kv => kv.Value with { Now = Solid(kv.Value.Mask, (byte)(10 + kv.Key.ImageId.Length)) }) };
        colored = colored with { Layers = colored.Layers.ToDictionary(kv => kv.Key, kv => kv.Value with {
            Packets = kv.Value.Packets.Select(p => p with { Images = p.Images.Select(i => colored.Images[i.Key]).ToArray(),
                Before = new ImageRgba(8, 1, new byte[32]), Current = new ImageRgba(8, 1, new byte[32]) }).ToArray(),
            Current = new ImageRgba(8, 1, new byte[32]) }) };
        var installer = new ClosureRecorder();
        var splitPlan = ReconstructionPlanner.Build(colored, split);
        var splitResult = ReconstructionEngine.RunAsync(splitPlan, installer, CancellationToken.None).GetAwaiter().GetResult();
        Check(splitPlan.IsValid && splitResult.Status == ReconstructionStatus.Executed &&
              splitPlan.Layers.Single().Segments.Single(s => s.Kind == ReconstructionSegmentKind.Replay).Events.Count == 2 &&
              splitPlan.Layers.Single().Segments.Single(s => s.Kind == ReconstructionSegmentKind.Compose).Images.Count == 2 &&
              installer.FinalReplay.Select(e => e.Stroke.Key).SequenceEqual([hair.Key]),
            "The engine pastes the images not replayed, then hands only the closure over to replay before the selection.");
        Check(installer.Expected is { } pasted && Pixel(pasted, 4) == 15 && Pixel(pasted, 6) == 13 && Pixel(pasted, 0) == 0 && Pixel(pasted, 1) == 0,
            "The pasted layer holds the mouth and the dot as recorded and leaves the replayed image at what lay beneath.");
        var stale = split with { Closure = new HashSet<EditableStrokeKey>() };
        Check(!ReconstructionPlanner.Build(colored, stale).IsValid,
            "A request whose closure no longer matches the replayed images is refused.");

        // Cross-layer search, one target layer: the current layer, else the nearest layer in the stack with strokes there.
        LayerKey Live(string uuid) => new(layer.SessionId, layer.Generation, uuid);
        var (below, current, above, top) = (Live("b"), Live("a"), Live("c"), Live("d"));
        var onCurrent = Stroke(current, 200, Mask(0)); var onBelow = Stroke(below, 201, Mask(5));
        var onAbove = Stroke(above, 202, Mask(5)); var onTop = Stroke(top, 203, Mask(6));
        var stacked = Index([Packet(current, "a", 10, Image(current, "a", "ia", Mask(0), [onCurrent])),
            Packet(below, "b", 11, Image(below, "b", "ib", Mask(5), [onBelow])),
            Packet(above, "c", 12, Image(above, "c", "ic", Mask(5), [onAbove])),
            Packet(top, "d", 13, Image(top, "d", "id", Mask(6), [onTop]))], [onCurrent, onBelow, onAbove, onTop]);
        string[] order = ["paper", "b", "a", "c", "d"];
        Check(PreciseUndoSelector.SelectNearest(stacked, current, order, Mask(0, 5)) is { IsValid: true } own &&
              own.CurrentLayer == current && own.Selected.Single().Key == onCurrent.Key,
            "Strokes of the current layer in the region keep precise undo on the current layer only.");
        Check(PreciseUndoSelector.SelectNearest(stacked, current, order, Mask(5)) is { IsValid: true } tie &&
              tie.CurrentLayer == above && tie.Selected.Single().Key == onAbove.Key,
            "Without current-layer strokes the nearest layer is undone, and only it; equally near, the layer above wins.");
        Check(PreciseUndoSelector.SelectNearest(stacked, current, order, Mask(6)) is { IsValid: true } far &&
              far.CurrentLayer == top && far.Selected.Single().Key == onTop.Key,
            "A farther layer is used only when every nearer layer has no strokes in the region.");
        Check(PreciseUndoSelector.SelectNearest(stacked, current, order, Mask(7)) is { IsValid: false } none && none.CurrentLayer == current,
            "A region without strokes on any layer reports that on the current layer.");
        Check(PreciseUndoSelector.SelectNearest(stacked, current, [], Mask(5)).CurrentLayer == current,
            "Without the confirmed layer stack no other layer is guessed.");

        Console.WriteLine($"Precise undo checks passed: {checks}.");
        return checks;
    }

    private sealed class ClosureRecorder : ICspReconstructionExecutor
    {
        internal IReadOnlyList<ReplayEvent> FinalReplay = [];
        internal ImageRgba? Expected;
        public Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
            IReadOnlyDictionary<LayerKey, ImageRgba> references, CancellationToken token)
            => throw new InvalidOperationException("Precise undo never replays before installing the reproduction layer.");
        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, CancellationToken token)
            => Task.FromResult(expected.Copy());
        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, IReadOnlyList<ReplayEvent> finalReplay, CancellationToken token)
        { FinalReplay = finalReplay; Expected = expected.Copy(); return Task.FromResult(expected.Copy()); }
    }

    private static PixelMask Mask(params int[] xs)
    {
        var mask = new PixelMask(8, 1);
        foreach (int x in xs) mask.Set(x, 0);
        return mask;
    }

    private static PixelMask CroppedMask(int x)
    {
        var mask = new PixelMask(x, 0, 1, 1);
        mask.Set(x, 0);
        return mask;
    }

    private static ImageRgba Solid(PixelMask mask, byte value)
    {
        var bytes = new byte[mask.Width * mask.Height * 4];
        Array.Fill(bytes, value);
        return new(mask.Width, mask.Height, bytes);
    }

    private static byte Pixel(ImageRgba image, int x) => image.Bytes[x * 4];

    private static EditableStroke Stroke(LayerKey layer, ulong eventId, PixelMask write)
        => new(new(layer.SessionId, 10000 - eventId), eventId, (long)(1000 - eventId), (long)(1001 - eventId), [], "Fixture pen", layer, false)
        { BeginEventId = eventId, WriteMask = write };

    private static DirtyImage Image(LayerKey layer, string packetId, string imageId, PixelMask mask, IReadOnlyList<EditableStroke> strokes)
        => new(new(layer, packetId, imageId), mask,
            new(mask.Width, mask.Height, new byte[mask.Width * mask.Height * 4]),
            new(mask.Width, mask.Height, new byte[mask.Width * mask.Height * 4]), strokes.Select(s => s.Key).ToArray());

    private static LayerPacket Packet(LayerKey layer, string id, long ticks, params DirtyImage[] images)
        => new(id, layer, ticks, null, null, images);

    private static EditIndex Index(IReadOnlyList<LayerPacket> packets, IReadOnlyList<EditableStroke> strokes)
    {
        // As the index builder does: a stroke knows the images its ink landed in.
        var all = packets.SelectMany(p => p.Images).ToArray();
        strokes = strokes.Select(s => s with { Images = all.Where(i => i.Events.Contains(s.Key)).Select(i => i.Key).ToArray() }).ToArray();
        var layers = packets.GroupBy(p => p.Layer).ToDictionary(g => g.Key,
            g => new LayerIndex(g.Key, g.ToArray(), strokes.Where(s => s.Layer == g.Key).ToArray(), true, []));
        return new(layers, strokes.ToDictionary(s => s.Key), packets.SelectMany(p => p.Images).ToDictionary(i => i.Key), []);
    }
}
