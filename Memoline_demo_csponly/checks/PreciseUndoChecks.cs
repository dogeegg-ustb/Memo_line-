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
        var earlierOverlap = Stroke(layer, 0, Mask(2));
        var foreign = Stroke(other, 100, Mask(0, 4));
        var candidateImage = Image(layer, "base", "candidate-image", Mask(0), candidates);
        var p0 = Packet(layer, "base", 10, candidateImage);
        var p1 = Packet(layer, "later-1", 20, Image(layer, "later-1", "i1", Mask(1), [firstAdded]));
        var p2 = Packet(layer, "later-2", 30, Image(layer, "later-2", "i2", Mask(2), [secondAdded]));
        var p3 = Packet(layer, "later-3", 40, Image(layer, "later-3", "i3", Mask(3), [thirdAdded]));
        var p4 = Packet(other, "foreign", 1000, Image(other, "foreign", "i4", Mask(0, 4), [foreign]));
        var all = candidates.Concat([firstAdded, secondAdded, thirdAdded, disjoint, earlierOverlap, foreign]).Reverse().ToArray();
        var index = Index([p3, p1, p4, p0, p2], all);
        var selection = PreciseUndoSelector.Select(index, layer, region);
        Check(selection.IsValid, "Scenario 17: selecting sixty eligible candidates must succeed.");
        Check(selection.CandidateCount == 60 && selection.BaseCount == 50,
            "Scenario 17: take the latest fifty candidates, not the oldest fifty or appendId order.");
        Check(selection.Selected.Select(s => s.BeginEventId).SequenceEqual(Enumerable.Range(11, 53).Select(n => (ulong)n)),
            "Scenario 17: transitive later overlap closure must replay 11..63 in original penBegin eventId order.");
        Check(selection.ClosureCount == 3 && selection.CountDisplay == "50 + 3",
            "Scenario 17: report the base fifty and the actual additional closure count.");
        Check(selection.Reasons.Select(r => r.Stroke).SequenceEqual(new[] { firstAdded.Key, secondAdded.Key, thirdAdded.Key }),
            "Each added stroke must have a distinct closure reason in replay order.");
        Check(selection.Reasons[0].OverlappingEarlierStroke == candidates[10].Key &&
              selection.Reasons[1].OverlappingEarlierStroke == firstAdded.Key &&
              selection.Reasons[2].OverlappingEarlierStroke == secondAdded.Key,
            "Closure reasons must identify the earlier selected stroke, including transitive additions.");
        Check(selection.Reasons.All(r => !string.IsNullOrWhiteSpace(r.Message)),
            "Closure additions expose an explanation for the control panel.");
        Check(!selection.Selected.Any(s => s.Key == earlierOverlap.Key || s.Key == disjoint.Key || s.Key == foreign.Key),
            "Earlier overlap, disconnected strokes and another layer must not enter the closure.");
        Check(selection.VisibleImages.SequenceEqual(new[] { candidateImage.Key }),
            "Only current-layer images with selected pixels are visible candidates; later other-layer masks do not cover them.");
        Check(region.Count == 1 && candidateImage.Mask.Count == 1 && firstAdded.WriteMask.Count == 2,
            "Selection must not mutate the input region, image masks or write footprints.");

        var few = Enumerable.Range(1, 7).Select(n => Stroke(layer, (ulong)n, Mask(0))).ToArray();
        var fewImage = Image(layer, "few", "few-image", Mask(0), few);
        var fewSelection = PreciseUndoSelector.Select(Index([Packet(layer, "few", 10, fewImage)], few.Reverse().ToArray()), layer, region);
        Check(fewSelection.IsValid && fewSelection.BaseCount == 7 && fewSelection.CandidateCount == 7,
            "Scenario 18: fewer than fifty candidates uses the actual number.");
        Check(fewSelection.Selected.Select(s => s.BeginEventId).SequenceEqual(Enumerable.Range(1, 7).Select(n => (ulong)n)) &&
              fewSelection.ClosureCount == 0 && fewSelection.CountDisplay == "7",
            "Scenario 18: original ordering and displayed count must not imply fifty strokes.");

        var oldStroke = Stroke(layer, 1, Mask(0, 1));
        var coveringStroke = Stroke(layer, 2, Mask(0));
        var oldImage = Image(layer, "old", "old-image", Mask(0, 1), [oldStroke]);
        var coverImage = Image(layer, "cover", "cover-image", Mask(0), [coveringStroke]);
        var visibilityIndex = Index([Packet(layer, "cover", 20, coverImage), Packet(layer, "old", 10, oldImage)], [coveringStroke, oldStroke]);
        var covered = PreciseUndoSelector.Select(visibilityIndex, layer, region);
        Check(covered.IsValid && covered.Selected.Select(s => s.Key).SequenceEqual(new[] { coveringStroke.Key }),
            "An older image covered at every selected pixel contributes no candidates, even if its W intersects the region.");
        Check(covered.VisibleImages.SequenceEqual(new[] { coverImage.Key }),
            "Visibility follows image masks and capture time, rather than input packet order or image alpha.");
        var partlyVisible = PreciseUndoSelector.Select(visibilityIndex, layer, Mask(0, 1));
        Check(partlyVisible.IsValid && partlyVisible.BaseCount == 2 && partlyVisible.VisibleImages.Count == 2,
            "One uncovered selected pixel is sufficient for an older image to remain a visible surface image.");

        var notWritingRegion = Stroke(layer, 3, Mask(5));
        var eligible = Stroke(layer, 4, Mask(0));
        var footprints = Image(layer, "footprints", "f", Mask(0), [notWritingRegion, eligible]);
        var writeSelected = PreciseUndoSelector.Select(Index([Packet(layer, "footprints", 10, footprints)], [notWritingRegion, eligible]), layer, region);
        Check(writeSelected.Selected.Select(s => s.Key).SequenceEqual(new[] { eligible.Key }),
            "Candidate hit testing must use W(s), not the dirty image change mask.");

        var croppedStroke = Stroke(layer, 5, CroppedMask(5));
        var croppedImage = Image(layer, "crop", "crop-image", CroppedMask(5), [croppedStroke]);
        var cropped = PreciseUndoSelector.Select(Index([Packet(layer, "crop", 10, croppedImage)], [croppedStroke]), layer, Mask(5));
        Check(cropped.IsValid && cropped.Selected.Single().Key == croppedStroke.Key,
            "Cropped image and W masks must intersect a full-canvas selection by canvas coordinates.");

        var duplicateStroke = Stroke(layer, 6, Mask(0, 1));
        var d0 = Image(layer, "d0", "d0-image", Mask(0), [duplicateStroke]);
        var d1 = Image(layer, "d1", "d1-image", Mask(1), [duplicateStroke]);
        var deduplicated = PreciseUndoSelector.Select(Index([Packet(layer, "d1", 20, d1), Packet(layer, "d0", 10, d0)], [duplicateStroke]), layer, Mask(0, 1));
        Check(deduplicated.IsValid && deduplicated.VisibleImages.Count == 2 && deduplicated.BaseCount == 1 && deduplicated.Selected.Count == 1,
            "One complete contact linked to multiple visible images is selected and replayed only once.");

        var earlierBad = Stroke(layer, 9, Mask(0)) with { Eligible = false, IneligibleReason = "Unconfirmed brush" };
        var good = Stroke(layer, 10, Mask(0));
        var goodImage = Image(layer, "eligibility", "good-image", Mask(0), [earlierBad, good]);
        var eligibilityIndex = Index([Packet(layer, "eligibility", 10, goodImage)], [earlierBad, good]);
        var onlyEligible = PreciseUndoSelector.Select(eligibilityIndex, layer, region);
        Check(onlyEligible.IsValid && onlyEligible.BaseCount == 1 && onlyEligible.Selected.Single().Key == good.Key,
            "Visible-image candidates must be eligible; an older ineligible stroke is not a later overlap dependency.");
        var laterBad = Stroke(layer, 11, Mask(0)) with { Eligible = false, IneligibleReason = "Unconfirmed brush" };
        var blocked = PreciseUndoSelector.Select(Index([Packet(layer, "eligibility", 10, goodImage)], [earlierBad, good, laterBad]), layer, region);
        Check(!blocked.IsValid && blocked.Issues.Any(i => i.Code == PlanIssueCode.UnreplayableEventInMatrix && i.Stroke == laterBad.Key),
            "A required later overlapping ineligible stroke must block precise undo instead of being silently skipped.");
        Check(blocked.BaseCount == 1 && blocked.ClosureCount == 1 && blocked.Selected.Any(s => s.Key == laterBad.Key),
            "The failed result identifies the actual selected closure that cannot be replayed.");

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
            "A region with no eligible visible-image W intersection reports no selectable strokes.");
        var baseline = Packet(layer, "baseline", 100, Image(layer, "baseline", "baseline-image", Mask(0), [])) with { IsRecordingBaseline = true };
        var baselineIgnored = PreciseUndoSelector.Select(Index([p0, baseline], candidates), layer, region);
        Check(baselineIgnored.IsValid && baselineIgnored.CandidateCount == 60,
            "A recording baseline is not a later dirty image and must not occlude eligible candidates.");

        var small = Enumerable.Range(1, 3).Select(n => Stroke(layer, (ulong)n, Mask(0, 1))).ToArray();
        var smallLater = Stroke(layer, 4, Mask(1));
        var smallImage = Image(layer, "small", "small-image", Mask(0), small);
        var smallClosure = PreciseUndoSelector.Select(Index([Packet(layer, "small", 10, smallImage)], small.Append(smallLater).ToArray()), layer, region);
        Check(smallClosure.IsValid && smallClosure.BaseCount == 3 && smallClosure.ClosureCount == 1 && smallClosure.CountDisplay == "3 + 1",
            "Fewer-than-fifty selection with closure must show its actual base plus additions.");

        Console.WriteLine($"Precise undo checks passed: {checks}.");
        return checks;
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
        var layers = packets.GroupBy(p => p.Layer).ToDictionary(g => g.Key,
            g => new LayerIndex(g.Key, g.ToArray(), strokes.Where(s => s.Layer == g.Key).ToArray(), true, []));
        return new(layers, strokes.ToDictionary(s => s.Key), packets.SelectMany(p => p.Images).ToDictionary(i => i.Key), []);
    }
}
