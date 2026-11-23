namespace ProcessTracker.Service.Updates;

/// <summary>The user-mode install's <c>app-&lt;X.Y.Z&gt;</c> folders under <c>%LOCALAPPDATA%\Programs\ProcessTracker</c>.</summary>
public static class AppFolders
{
    public const string Prefix = "app-";
    public const string RootName = "ProcessTracker";

    public static bool IsAppFolderName(string? name) =>
        name is not null && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The full path without a trailing separator, for comparisons.</summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes a folder and everything in it. Any folder that is a link (junction or symbolic link), at the top or
    /// inside, is unlinked; its target is never entered. (Directory.Delete's own recursion fails on a junction inside
    /// the tree for a non-admin user, because it first tries to unmount it as a volume.)
    /// </summary>
    public static void Delete(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists) DeleteTree(info);
    }

    private static void DeleteTree(DirectoryInfo dir)
    {
        if (!dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo sub) DeleteTree(sub);
                else entry.Delete();
            }
        }
        dir.Delete(recursive: false); // removes a link itself, or the now empty folder
    }
}
