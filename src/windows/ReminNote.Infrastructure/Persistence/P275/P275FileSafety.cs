namespace ReminNote.Infrastructure.Persistence.P275;

/// <summary>
/// Small common file-policy helpers for the migration boundary. A path that
/// resolves through a reparse point is not a database generation or sidecar
/// owned by this profile, even when its lexical name is inside the profile.
/// </summary>
internal static class P275FileSafety
{
    public static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            // An inaccessible or unstable path is treated as present so the
            // caller fails closed instead of ignoring a possible sidecar.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static bool IsRegularFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
