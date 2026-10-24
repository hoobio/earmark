namespace Earmark.Core.Persistence;

internal static class CorruptCopy
{
    public static string? TryCreate(string path)
    {
        var copy = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try
        {
            File.Copy(path, copy, overwrite: true);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
