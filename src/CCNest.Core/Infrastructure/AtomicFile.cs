using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCNest.Core.Infrastructure;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>
/// Crash-safe file writes: data goes to a temp file in the same directory, is flushed to disk,
/// and then atomically replaces the target. A torn write can never corrupt the original.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, bool keepBackup = false)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }

        try
        {
            if (File.Exists(path))
            {
                var backup = keepBackup ? path + ".bak" : null;
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            try { File.Delete(temp); } catch { /* ignore */ }
            throw;
        }
    }

    public static void WriteJson<T>(string path, T value, bool keepBackup = false) =>
        WriteAllText(path, JsonSerializer.Serialize(value, JsonDefaults.Options), keepBackup);

    public static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options);
        }
        catch (Exception ex)
        {
            Log.Error("AtomicFile", $"无法解析 {path}", ex);
            var bak = path + ".bak";
            if (File.Exists(bak))
            {
                try { return JsonSerializer.Deserialize<T>(File.ReadAllText(bak), JsonDefaults.Options); }
                catch { /* fall through */ }
            }
            return null;
        }
    }
}
