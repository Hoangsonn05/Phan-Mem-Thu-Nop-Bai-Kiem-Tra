using System.Diagnostics;
using System.IO;

namespace ExamTransfer.Desktop.Services;

public sealed class LocalFileLauncher : ILocalFileLauncher
{
    public bool Exists(string path) => File.Exists(path);

    public void Open(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Tệp đã tải không còn tồn tại.", path);

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }
}
