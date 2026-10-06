using System.Text.Json.Nodes;

namespace MemolineDemo;

internal static class InputSelectionChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException(reason); count++; }
        var selected = new RecorderInputSelection(false, "tablet-a", "driver.xml", false, 2);
        Check(selected.Arguments.SequenceEqual(new[] { "--tablet-device-id", "tablet-a" }), "OTD selection must reach the recorder command");
        var passive = selected with { PassivePen = true };
        Check(passive.Arguments.SequenceEqual(new[] { "--passive-pen" }), "Windows pen cannot carry an OTD device selection");
        Check((selected with { DeviceId = null }).Arguments.Length == 0, "Automatic mode keeps original recorder defaults");
        var settings = new JsonObject { ["clipPath"] = "document.clip", ["analysisQuietMs"] = 150 };
        selected.Apply(settings);
        Check(settings["driverConfigPath"]!.GetValue<string>() == "driver.xml"
            && settings["driverScreenIndex"]!.GetValue<int>() == 2 && !settings["driverMappingDisabled"]!.GetValue<bool>(), "Mapping selection must reach native setup");
        var disabled = selected with { DriverPath = null, DriverDisabled = true, ScreenIndex = null };
        disabled.Apply(settings);
        Check(settings["driverConfigPath"] is null && settings["driverScreenIndex"] is null
            && settings["driverMappingDisabled"]!.GetValue<bool>(), "Disabling mapping must clear previously selected values");
        Check(settings["clipPath"]!.GetValue<string>() == "document.clip" && settings["analysisQuietMs"]!.GetValue<int>() == 150,
            "Input choices must preserve unrelated recorder settings");
        return count;
    }
}
