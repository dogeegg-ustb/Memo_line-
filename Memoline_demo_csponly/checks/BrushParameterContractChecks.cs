using System.Drawing;
using System.Text.Json;
using MemolineDemo.Editing;
using PacketReplay;

namespace MemolineDemo;

internal static class BrushParameterContractChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); count++; }
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
        var catalog = new ShortcutConfiguration([
            new("tool_pen", "Pen", null, "P"), new("tool_replacement", "Replacement", null, "R")]);
        var historical = new BrushSnapshot("Pen", [new("size", 3), new("opacity", 80), new("hidden", 7)], 0);
        var live = new BrushSnapshot("Pen", [new("size", 6, new Rectangle(100, 50, 30, 15)),
            new("opacity", 70, new Rectangle(100, 70, 30, 15)), new("hidden", 7)], 0);
        var available = BrushParameterContract.Evaluate([historical], live, null, catalog);
        Check(available.TargetRouteAvailable && !available.RequiresActiveBrushConfirmation,
            "A uniquely routable, actively confirmed brush must support numeric edits.");
        Check(available.Numbers.Select(n => n.Key).SequenceEqual(["opacity", "size"]),
            "Only historical numeric keys with confirmed live input boxes may be displayed.");
        Check(available.Numbers.Single(n => n.Key == "size").RecordedValue == 3 &&
              available.Numbers.Single(n => n.Key == "size").LiveValue == 6,
            "The edit form must distinguish recorded values from current editor values.");
        var second = historical with { Numbers = [new("size", 9)] };
        available = BrushParameterContract.Evaluate([historical, second], live, null, catalog);
        Check(available.Numbers.Count == 1 && available.Numbers[0].Key == "size" && available.Numbers[0].RecordedValuesDiffer,
            "Multiple contacts expose only common numeric keys and preserve differing recorded values.");
        var replacement = live with { Name = "Replacement" };
        available = BrushParameterContract.Evaluate([historical], live, "Replacement", catalog);
        Check(available.TargetRouteAvailable && available.RequiresActiveBrushConfirmation && available.Numbers.Count == 0,
            "A new subtool can be routed, but its editors must be actively confirmed before numeric editing.");
        available = BrushParameterContract.Evaluate([historical], replacement, "Replacement", catalog);
        Check(available.Numbers.Count == 2 && !available.RequiresActiveBrushConfirmation,
            "Combined subtool and numeric edits become available after confirming the replacement editors.");
        replacement = replacement with { Numbers = [new("flow", 25, new Rectangle(100, 90, 30, 15))] };
        available = BrushParameterContract.Evaluate([historical], replacement, "Replacement", catalog);
        Check(available.Numbers.Count == 1 && available.Numbers[0].Key == "flow" && available.Numbers[0].RecordedValue == 25,
            "A confirmed replacement may expose its own writable keys even when the original brush lacks them.");
        Check(BrushParameterContract.Evaluate([historical], live, "Replacement", catalog).Numbers.Count == 0,
            "Replacement-only properties require confirmation of that target, never the original tool's panel.");
        available = BrushParameterContract.Evaluate([historical, historical with { Name = "Replacement" }], live, null, catalog);
        Check(available.Numbers.Count == 0 && available.RequiresActiveBrushConfirmation,
            "One current panel cannot prove editable keys for several different historical subtools.");
        available = BrushParameterContract.Evaluate([historical], live, "Missing", catalog);
        Check(!available.TargetRouteAvailable && available.Numbers.Count == 0,
            "A missing shortcut route must be rejected before offering a subtool override.");
        available = BrushParameterContract.Evaluate([null], live, null, catalog);
        Check(available.Numbers.Count == 0 && !available.TargetRouteAvailable,
            "A missing full historical snapshot cannot be replaced by the stroke's numeric dictionary.");
        available = BrushParameterContract.Evaluate([], live, null, catalog);
        Check(available.Numbers.Count == 0 && !available.RequiresActiveBrushConfirmation,
            "An empty stroke selection does not require an active confirmation.");
        available = BrushParameterContract.Evaluate([historical], null, null, catalog);
        Check(available.Numbers.Count == 0 && available.RequiresActiveBrushConfirmation,
            "Missing live brush confirmation must never enable numeric editing.");
        available = BrushParameterContract.Evaluate([historical], live with { Numbers = [new("size", 6, Rectangle.Empty)] }, null, catalog);
        Check(available.Numbers.Count == 0, "A zero-sized input rectangle must not count as an editor.");
        var raw = Json("""
            {"status":"changed","state":{"name":"Pen","properties":[
              {"key":"size","type":"number","status":"ok","value":3},
              {"key":"disabled","type":"number","status":"ok","enabled":"disabled","value":4},
              {"key":"icon","type":"number","value_category":"icon","status":"ok","value":2},
              {"key":"option","type":"option","status":"ok","value":2}]},
             "valueRegions":[
               {"propertyKey":"size","category":"number","status":"ok","value":3,"screenBbox":[20,30,50,45]},
               {"propertyKey":"size","category":"number","status":"ok","value":3,"screenBbox":[20,60,50,75]}]}
            """);
        var parsed = BrushSnapshot.Parse(raw, live: true)!;
        Check(parsed.Numbers.Count == 1 && parsed.Numbers[0].Input is null,
            "The existing packet parser must exclude icon/option/disabled values and ambiguous live editors.");
        Check(BrushParameterContract.Evaluate([historical], parsed, null, catalog).Numbers.Count == 0,
            "Duplicate valueRegions must not leak into the edit form as writable numbers.");
        return count;
    }
}
