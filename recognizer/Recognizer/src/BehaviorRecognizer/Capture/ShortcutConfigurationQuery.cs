using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BehaviorRecognizer.Capture;

/// <summary>Read the same saved configuration as the recorder, without starting capture.</summary>
public static class ShortcutConfigurationQuery
{
    public static async Task<JsonElement> ReadAsync(string? configDirectory = null, CancellationToken token = default)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "integration", "shortcut_api.py");
        if (!File.Exists(script)) throw new FileNotFoundException("Missing integration/shortcut_api.py; rebuild the recorder.", script);
        string? python = System.Environment.GetEnvironmentVariable("MEMOLINE_PYTHON");
        var start = new ProcessStartInfo(python ?? "py")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        if (python is null) start.ArgumentList.Add("-3");
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(script);
        if (configDirectory is not null)
        {
            start.ArgumentList.Add("--config-dir");
            start.ArgumentList.Add(Path.GetFullPath(configDirectory));
        }
        start.Environment["PYTHONUTF8"] = "1";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("Unable to query CSP shortcut configuration.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false), error = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("CSP 快捷键查询失败: " + error.Trim());
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            // Observe pending reads even if cancellation interrupted the query.
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}
