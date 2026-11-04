namespace StationGuard;

/// <summary>
/// Keeps a folder of saved images under a file count, oldest first, together
/// with each image's side files (the same name with another extension, such as
/// its .json). For folders written by code that does not use
/// <see cref="NgImageStore"/>, such as a camera SDK's own image saver.
/// </summary>
public static class FolderQuota
{
    /// <summary>Deletes the oldest images beyond <paramref name="maxFiles"/>; returns how many went.</summary>
    public static int Trim(string folder, string pattern, int maxFiles, params string[] sideExtensions)
    {
        if (!Directory.Exists(folder)) return 0;
        // Only the folder itself and only the saver's own file pattern: a folder that also holds
        // other images (a labelled set, another station's folders) never loses them.
        var files = new DirectoryInfo(folder).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc).Skip(Math.Max(0, maxFiles)).ToList();
        var removed = 0;
        foreach (var f in files)
        {
            try
            {
                f.Delete();
                foreach (var ext in sideExtensions) File.Delete(Path.ChangeExtension(f.FullName, ext));
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>Trims the folder every <paramref name="every"/> on a background thread of below-normal priority.</summary>
    public static IDisposable Keep(string folder, string pattern, int maxFiles, TimeSpan every, params string[] sideExtensions)
    {
        var stop = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            while (!stop.Wait(every))
                try { Trim(folder, pattern, maxFiles, sideExtensions); } catch (Exception) { }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "folder-quota" };
        t.Start();
        return new Stopper(stop, t);
    }

    sealed class Stopper(ManualResetEventSlim stop, Thread t) : IDisposable
    {
        public void Dispose() { stop.Set(); t.Join(TimeSpan.FromSeconds(2)); stop.Dispose(); }
    }
}
