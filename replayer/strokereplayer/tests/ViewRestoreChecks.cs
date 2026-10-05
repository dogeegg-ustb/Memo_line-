using System.Drawing;
using StrokeReplay;

internal static class ViewRestoreChecks
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var baseView = View();
        Check(ViewRestorer.SameRotation(baseView with { RotationDegrees = -180 }, baseView with { RotationDegrees = 180 }),
            "Equivalent signed rotation angles must match.");
        Check(ViewRestorer.SameRotation(baseView with { RotationDegrees = 359.98 }, baseView with { RotationDegrees = 0 }),
            "Rotation tolerance must wrap at a full turn.");
        Check(!ViewRestorer.SameRotation(baseView with { RotationDegrees = 359.8 }, baseView),
            "A material rotation error must not match.");

        await UseMatchingCurrentWithoutUpdateAsync();
        await UseMatchReceivedDuringInputAsync();
        await RestoreWithFeedbackAsync();
        await RejectSizeMismatchAsync();
        await RejectMissingInputAsync();
        await RecoverTransformDriftAsync();
        await CancelBeforeInputAsync();
        Console.WriteLine("View restore checks passed.");
    }

    private static async Task UseMatchingCurrentWithoutUpdateAsync()
    {
        var current = View();
        var feed = new FakeFeed(current);
        var input = new FakeInput(feed);
        await new ViewRestorer(feed, input, _ => { }).RestoreAsync(current, CancellationToken.None);
        Check(feed.WaitRequests.Count == 0 && input.Numbers.Count == 0 && input.Pans.Count == 0,
            "An already matching confirmed view must proceed without requiring a new update or input.");
    }

    private static async Task UseMatchReceivedDuringInputAsync()
    {
        var current = View();
        var feed = new FakeFeed(current) { PublishImmediately = true };
        var input = new FakeInput(feed);
        var target = current with { OriginX = current.OriginX + 100 };
        await new ViewRestorer(feed, input, _ => { }).RestoreAsync(target, CancellationToken.None);
        Check(input.Pans.Count == 1 && feed.WaitRequests.Count == 0 && feed.CompletionMarks == 1,
            "A matching confirmed observation received during input must not be discarded or await another update.");
    }

    private static async Task RestoreWithFeedbackAsync()
    {
        var current = View();
        var feed = new FakeFeed(current);
        var input = new FakeInput(feed);
        var target = current with
        {
            ScalePercent = 75,
            RotationDegrees = -27,
            OriginX = 1280,
            OriginY = -930,
            Viewport = new Rectangle(10000, 10000, 200, 200),
            ZoomInput = new Rectangle(11000, 11000, 50, 20),
            RotationInput = new Rectangle(12000, 12000, 50, 20)
        };
        var statuses = new List<string>();
        await new ViewRestorer(feed, input, statuses.Add).RestoreAsync(target, CancellationToken.None);

        Check(ViewRestorer.Matches(feed.Current!, target), "All view components must be confirmed before restoration finishes.");
        Check(input.Numbers.Count == 2, "Restore scale and rotation through their numeric fields.");
        Check(input.Numbers[0] == (current.ZoomInput!.Value, target.ScalePercent),
            "Zoom input coordinates must come from the current Recognizer view.");
        Check(input.Numbers[1].Roi == input.RotationAfterZoom,
            "Rotation must use the field location returned by the observation after zoom.");
        Check(input.Pans.Count > 1, "Large displacement must be restored through bounded drags.");
        foreach (var (from, to) in input.Pans)
        {
            Check(current.Viewport!.Value.Contains(from) && current.Viewport.Value.Contains(to),
                "Both drag endpoints must stay in the current viewport, including negative screen coordinates.");
            Check(Math.Abs(to.X - from.X) <= current.Viewport.Value.Width * .4 + 1 &&
                  Math.Abs(to.Y - from.Y) <= current.Viewport.Value.Height * .4 + 1,
                "A drag must be bounded by the current viewport.");
        }
        Check(feed.WaitRequests.Skip(1).All(revision => revision >= 0),
            "Every action must wait for feedback beyond an existing observation.");
        Check(feed.WaitRequests.Count == input.Numbers.Count + input.Pans.Count,
            "Each restore action must have a corresponding Recognizer confirmation.");
        Check(feed.CompletionMarks == input.Numbers.Count + input.Pans.Count,
            "Each action must establish a capture barrier before input delivery.");
        Check(statuses.Count >= 3, "Restoration must report zoom, rotation and pan progress.");
    }

    private static async Task RejectSizeMismatchAsync()
    {
        var current = View();
        var feed = new FakeFeed(current);
        var input = new FakeInput(feed);
        await ThrowsAsync<InvalidOperationException>(() => new ViewRestorer(feed, input, _ => { })
            .RestoreAsync(current with { PixelWidth = current.PixelWidth + 1 }, CancellationToken.None));
        Check(input.Numbers.Count == 0 && input.Pans.Count == 0, "Different document dimensions must fail before an edit.");
    }

    private static async Task RejectMissingInputAsync()
    {
        var current = View() with { ZoomInput = null };
        var feed = new FakeFeed(current);
        var input = new FakeInput(feed);
        await ThrowsAsync<InvalidOperationException>(() => new ViewRestorer(feed, input, _ => { })
            .RestoreAsync(current with { ScalePercent = 75 }, CancellationToken.None));
        Check(input.Numbers.Count == 0 && input.Pans.Count == 0, "A missing current OCR field must fail before injection.");
    }

    private static async Task RecoverTransformDriftAsync()
    {
        var current = View();
        var feed = new FakeFeed(current);
        var input = new FakeInput(feed) { DriftScaleOnPan = true };
        var target = current with { OriginX = current.OriginX + 1000 };
        await new ViewRestorer(feed, input, _ => { }).RestoreAsync(target, CancellationToken.None);
        Check(input.Pans.Count > 1 && input.Numbers.Count > 0 && ViewRestorer.Matches(feed.Current!, target),
            "Restoration must replan from confirmed changed scale and origin instead of abandoning recovery.");
    }

    private static async Task CancelBeforeInputAsync()
    {
        var current = View();
        var feed = new FakeFeed(current);
        var input = new FakeInput(feed);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => new ViewRestorer(feed, input, _ => { })
            .RestoreAsync(current with { ScalePercent = 75 }, cancelled.Token));
        Check(input.Numbers.Count == 0 && input.Pans.Count == 0, "Cancellation must prevent all restore input.");
    }

    private static CanvasViewSnapshot View() => new(50, 0, -400, -200,
        new Rectangle(-900, -500, 400, 300), new Rectangle(-120, -400, 70, 20),
        new Rectangle(-120, -380, 70, 20), 1920, 1080);

    private sealed class FakeFeed(CanvasViewSnapshot initial) : ICanvasViewFeed
    {
        public CanvasViewSnapshot? Current { get; private set; } = initial;
        public long Revision { get; private set; } = 1;
        internal List<long> WaitRequests { get; } = [];
        internal int CompletionMarks { get; private set; }
        private CanvasViewSnapshot? _pending;
        internal bool PublishImmediately { get; init; }
        public void EnsureConnected() { }
        internal void QueueObservation(CanvasViewSnapshot view)
        {
            if (PublishImmediately) { Current = view; Revision++; }
            else _pending = view;
        }
        public void MarkInputStarted()
        {
            CompletionMarks++;
            Check(_pending is null, "The capture barrier must precede input delivery.");
        }
        public Task<CanvasViewSnapshot> WaitForViewAsync(long afterRevision, Func<CanvasViewSnapshot, bool> predicate,
            TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            WaitRequests.Add(afterRevision);
            if (_pending is { } observation) { Current = observation; _pending = null; Revision++; }
            Check(Revision > afterRevision, "The fake core must supply a new observation after each input.");
            Check(predicate(Current!), "Restore must await a matching view, rather than finish after input delivery.");
            return Task.FromResult(Current!);
        }
    }

    private sealed class FakeInput(FakeFeed feed) : IViewInput
    {
        internal List<(Rectangle Roi, double Value)> Numbers { get; } = [];
        internal List<(Point From, Point To)> Pans { get; } = [];
        internal Rectangle RotationAfterZoom { get; } = new(-125, -375, 72, 21);
        internal bool DriftScaleOnPan { get; init; }
        public void EnsureTarget() { }
        public Task SetNumberAsync(Rectangle input, double value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Numbers.Add((input, value));
            var current = feed.Current!;
            if (input == current.ZoomInput)
                feed.QueueObservation(current with { ScalePercent = value, OriginX = current.OriginX + 37,
                    OriginY = current.OriginY + 29, RotationInput = RotationAfterZoom });
            else if (input == current.RotationInput)
                feed.QueueObservation(current with { RotationDegrees = value, OriginX = current.OriginX + 11,
                    OriginY = current.OriginY - 19 });
            else throw new InvalidOperationException("Restore used a recorded or obsolete OCR location.");
            return Task.CompletedTask;
        }
        public Task PanAsync(Point from, Point to, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Pans.Add((from, to));
            var current = feed.Current!;
            feed.QueueObservation(current with { OriginX = current.OriginX + to.X - from.X,
                OriginY = current.OriginY + to.Y - from.Y,
                ScalePercent = current.ScalePercent + (DriftScaleOnPan && Pans.Count == 1 ? 1 : 0) });
            return Task.CompletedTask;
        }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

