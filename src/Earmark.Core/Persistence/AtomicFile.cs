namespace Earmark.Core.Persistence;

internal static class AtomicFile
{
    private const int Attempts = 5;

    public static async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < Attempts && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            // OneDrive and antivirus scanners hold the file briefly; a short backoff clears it.
            catch (Exception ex) when (attempt < Attempts && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100 * attempt, ct).ConfigureAwait(false);
            }
        }
    }
}
