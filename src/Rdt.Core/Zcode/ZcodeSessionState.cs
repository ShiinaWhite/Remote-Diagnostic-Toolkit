using System.Text.Json;
using Rdt.Common;

namespace Rdt.Zcode;

internal static class ZcodeSessionState
{
    internal static void VerifySelection(JsonElement snapshot, string model)
    {
        try
        {
            var settings = snapshot.GetProperty("settings");
            var selected = settings.GetProperty("model").GetProperty("current");
            if (settings.GetProperty("mode").GetProperty("current").GetString() != "yolo" ||
                settings.GetProperty("permission").GetProperty("mode").GetString() != "yolo" ||
                selected.GetProperty("providerId").GetString() != "rdt-g" || selected.GetProperty("modelId").GetString() != model)
                throw new RdtException("SESSION_SELECTION_MISMATCH");
        }
        catch (RdtException) { throw; }
        catch { throw new RdtException("PROTOCOL_MALFORMED"); }
    }

    internal static string? Observe(JsonElement snapshot, HashSet<string> seenTools, Action<string> progress)
    {
        try
        {
            var projection = snapshot.GetProperty("projection");
            if (projection.TryGetProperty("lastError", out var error)) throw new RdtException(ClassifyProviderError(error));
            string? final = null;
            foreach (var message in snapshot.GetProperty("messages").EnumerateArray())
            {
                var info = message.GetProperty("info");
                if (info.TryGetProperty("error", out var messageError)) throw new RdtException(ClassifyProviderError(messageError));
                if (info.GetProperty("role").GetString() != "assistant") continue;
                var text = new System.Text.StringBuilder();
                foreach (var part in message.GetProperty("parts").EnumerateArray())
                {
                    var type = part.GetProperty("type").GetString();
                    if (type == "tool" && part.TryGetProperty("callId", out var id) && seenTools.Add(id.GetString()!))
                        progress("[Tool] " + (part.TryGetProperty("tool", out var tool) ? tool.GetString() : "Working"));
                    if (type == "text" && part.TryGetProperty("text", out var content)) text.Append(content.GetString());
                }
                if (info.TryGetProperty("finish", out var finish) && finish.GetString() is "stop" &&
                    info.TryGetProperty("time", out var time) && time.TryGetProperty("completed", out _) && text.Length > 0)
                    final = text.ToString();
            }
            if (projection.GetProperty("status").GetString() == "error") throw new RdtException("PROVIDER_FAILURE");
            return projection.GetProperty("status").GetString() == "idle" ? final : null;
        }
        catch (RdtException) { throw; }
        catch { throw new RdtException("PROTOCOL_MALFORMED"); }
    }

    internal static string ClassifyProviderError(JsonElement error)
    {
        // Inspect in memory; never return free-form provider text or serialized frames.
        var text = error.GetRawText();
        if (text.Contains("401", StringComparison.OrdinalIgnoreCase) || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authentication", StringComparison.OrdinalIgnoreCase) || text.Contains("invalid api key", StringComparison.OrdinalIgnoreCase))
            return "PROVIDER_AUTH_ERROR";
        if (text.Contains("network", StringComparison.OrdinalIgnoreCase) || text.Contains("ECONN", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fetch failed", StringComparison.OrdinalIgnoreCase) || text.Contains("ENOTFOUND", StringComparison.OrdinalIgnoreCase))
            return "PROVIDER_NETWORK_ERROR";
        return "PROVIDER_FAILURE";
    }
}
