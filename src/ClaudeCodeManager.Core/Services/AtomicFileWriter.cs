using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCodeManager.Core.Services;

public static class AtomicFileWriter
{
    public static async Task WriteAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            var bak = path + ".bak";
            try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
            try { File.Delete(bak); } catch { }
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    public static void Write(string path, string content)
    {
        WriteAsync(path, content).GetAwaiter().GetResult();
    }
}
