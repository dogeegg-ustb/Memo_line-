using System.Text.Json;
using System.Text.Json.Nodes;

namespace BehaviorRecognizer.Storage.Memoline;

/// <summary>Persist confirmed core states; raw attempts remain diagnostic records.</summary>
internal static class MemolineStatePersistence
{
    public static bool IsSuccessfulUpdate(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty("status", out var status) && status.GetString() is "changed" or "unchanged" &&
        data.TryGetProperty("state", out var state) && state.ValueKind != JsonValueKind.Null &&
        (!data.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null);

    public static bool IsDiagnostic(string kind, object data) => kind switch
    {
        "coreStateUpdated" => !IsSuccessfulUpdate(Element(data)),
        // Successful raw results are already included in coreStateUpdated.rawResult.
        "coreEvidenceCaptured" or "stateResult" or "analysisError" or "captureUnavailable" or "clipParseError" or "updateActivatorError" => true,
        "initializationStatus" => Element(data).TryGetProperty("status", out var status) && status.GetString() == "failed",
        _ => false
    };

    private static JsonElement Element(object data) => data is JsonElement element
        ? element : JsonSerializer.SerializeToElement(data, MemolineWriter.Json);

    public static JsonObject? SuccessfulPackage(JsonElement data)
    {
        if (!data.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array)
            return null;
        var confirmed = updates.EnumerateArray().Where(IsSuccessfulUpdate).ToArray();
        if (confirmed.Length == 0) return null;
        var result = JsonNode.Parse(data.GetRawText())!.AsObject();
        result["status"] = confirmed.Any(u => u.GetProperty("status").GetString() == "changed") ? "changed" : "unchanged";
        result["updates"] = new JsonArray(confirmed.Select(u => JsonNode.Parse(u.GetRawText())).ToArray());
        result.Remove("error");
        result.Remove("observedState");
        return result;
    }
}
