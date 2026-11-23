using System.Diagnostics;
using System.Text;

namespace SmartGallery.Infrastructure.Video;

internal static class VideoToolProcess
{
    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken token, Action<string>? onLine = null, bool showWindow = false)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = !showWindow,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new IOException($"Nelze spustit {Path.GetFileName(executable)}.");
        var output = onLine is null
            ? ReadBoundedAsync(process.StandardOutput, 1024 * 1024, token)
            : ReadProgressAsync(process.StandardOutput, onLine, token);
        var error = ReadBoundedAsync(process.StandardError, 16 * 1024, token, truncate: true);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            var diagnostics = await error.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                var message = diagnostics.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase)
                    ? "Ve vstupním souboru jsou nečitelná mediální data."
                    : $"{Path.GetFileNameWithoutExtension(executable)} nedokončil zpracování (kód {process.ExitCode}).";
                throw new IOException(message, new InvalidDataException(diagnostics.Trim()));
            }
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token,
        bool truncate = false)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        var exceeded = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            var retained = Math.Min(count, limit - result.Length);
            result.Append(buffer, 0, retained);
            exceeded |= retained != count;
        }
        return exceeded && !truncate ? throw new IOException("Výstup mediálního nástroje je příliš dlouhý.")
            : result.ToString();
    }

    private static async Task<string> ReadProgressAsync(StreamReader reader, Action<string> onLine, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            onLine(line);
        }
        return "";
    }
}
