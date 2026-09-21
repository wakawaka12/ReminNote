using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using System.IO;
using Microsoft.Win32;

namespace ReminNote.Windows.Notifications;

/// <summary>
/// Registers the unpackaged desktop executable with the user's Start-menu
/// shortcut and writes the same AUMID that WinRT ToastNotificationManager uses
/// into the shortcut property store. The result is read back before it is
/// advertised as verified; failures stay fail-closed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsToastRegistration
{
    private const ushort VariantTypeBstr = 8;
    private const ushort VariantTypeLpWStr = 31;
    private static readonly PropertyKey ApplicationUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    public static bool TryEnsureAndVerify(
        string applicationUserModelId,
        string? executablePath,
        out string? failureCode,
        string? dataRoot = null,
        string? profileName = null)
    {
        failureCode = null;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(applicationUserModelId))
        {
            failureCode = "windows.toast.registration.platform_unavailable";
            return false;
        }

        var target = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            failureCode = "windows.toast.registration.target_missing";
            return false;
        }

        target = Path.GetFullPath(target);
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        if (string.IsNullOrWhiteSpace(startMenu))
        {
            failureCode = "windows.toast.registration.start_menu_missing";
            return false;
        }

        var programs = Path.Combine(startMenu, "Programs");
        var shortcut = Path.Combine(programs, "ReminNote.lnk");
        var temporary = shortcut + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(programs);
            if (!IsRegularDirectory(programs) || (File.Exists(shortcut) && !IsRegularFile(shortcut)))
            {
                failureCode = "windows.toast.registration.path_invalid";
                return false;
            }

            WriteShortcut(temporary, target, applicationUserModelId);
            if (!VerifyShortcut(temporary, target, applicationUserModelId))
            {
                failureCode = "windows.toast.registration.verification_failed";
                return false;
            }

            File.Move(temporary, shortcut, overwrite: true);
            if (!VerifyShortcut(shortcut, target, applicationUserModelId))
            {
                failureCode = "windows.toast.registration.verification_failed";
                return false;
            }

            if (!TryEnsureAndVerifyProtocolRegistration(
                    target,
                    dataRoot,
                    profileName,
                    out failureCode))
            {
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failureCode = exception switch
            {
                UnauthorizedAccessException => "windows.toast.registration.access_denied",
                IOException => "windows.toast.registration.io_failed",
                COMException => "windows.toast.registration.com_failed",
                _ => "windows.toast.registration.failed"
            };
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Registers the same executable as the handler for the bounded
    /// <c>reminnote://</c> launch URI used by Toasts. The command line carries
    /// the already-selected isolated data root/profile so a protocol launch
    /// does not depend on the shell's current directory.
    /// </summary>
    private static bool TryEnsureAndVerifyProtocolRegistration(
        string executablePath,
        string? dataRoot,
        string? profileName,
        out string? failureCode)
    {
        failureCode = null;
        if (executablePath.Contains('"') ||
            dataRoot?.Contains('"') == true ||
            profileName?.Contains('"') == true ||
            executablePath.Contains('\0') ||
            dataRoot?.Contains('\0') == true ||
            profileName?.Contains('\0') == true)
        {
            failureCode = "windows.toast.registration.protocol.path_invalid";
            return false;
        }

        try
        {
            var command = BuildProtocolCommand(executablePath, dataRoot, profileName);
            using (var protocol = Registry.CurrentUser.CreateSubKey(
                       @"Software\Classes\reminnote",
                       writable: true))
            {
                if (protocol is null)
                {
                    failureCode = "windows.toast.registration.protocol.unavailable";
                    return false;
                }

                protocol.SetValue(string.Empty, "URL:ReminNote Protocol");
                protocol.SetValue("URL Protocol", string.Empty);
                using var shell = protocol.CreateSubKey(@"shell\open\command", writable: true);
                if (shell is null)
                {
                    failureCode = "windows.toast.registration.protocol.unavailable";
                    return false;
                }

                shell.SetValue(string.Empty, command);
            }

            using var verified = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\reminnote\shell\open\command",
                writable: false);
            var registered = verified?.GetValue(string.Empty) as string;
            if (!string.Equals(registered, command, StringComparison.Ordinal))
            {
                failureCode = "windows.toast.registration.protocol.verification_failed";
                return false;
            }

            // URL associations are cached by the Shell.  A registry write can
            // therefore verify successfully while an already-running Shell
            // still reports that no app handles the scheme.  Invalidate the
            // association cache before advertising the Toast channel as
            // verified.
            NotifyShellAssociationChanged();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failureCode = exception switch
            {
                UnauthorizedAccessException => "windows.toast.registration.protocol.access_denied",
                IOException => "windows.toast.registration.protocol.io_failed",
                System.Security.SecurityException => "windows.toast.registration.protocol.access_denied",
                _ => "windows.toast.registration.protocol.failed"
            };
            return false;
        }
    }

    private static string BuildProtocolCommand(
        string executablePath,
        string? dataRoot,
        string? profileName)
    {
        var arguments = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            arguments.Append(" --data-root ").Append(QuoteCommandLineValue(dataRoot));
        }

        if (!string.IsNullOrWhiteSpace(profileName))
        {
            arguments.Append(" --profile ").Append(QuoteCommandLineValue(profileName));
        }

        arguments.Append(" \"%1\"");
        return QuoteCommandLineValue(executablePath) + arguments;
    }

    private static string QuoteCommandLineValue(string value) => $"\"{value}\"";

    private static void NotifyShellAssociationChanged() =>
        SHChangeNotify(
            ShellChangeAssociationChanged,
            ShellChangeNotifyIdList,
            IntPtr.Zero,
            IntPtr.Zero);

    private static void WriteShortcut(
        string shortcutPath,
        string executablePath,
        string applicationUserModelId)
    {
        object? linkObject = null;
        IPropertyStore? propertyStore = null;
        PropVariant value = default;
        try
        {
            linkObject = new ShellLink();
            var link = (IShellLinkW)linkObject;
            ThrowIfFailed(link.SetPath(executablePath), "IShellLinkW.SetPath");
            ThrowIfFailed(
                link.SetWorkingDirectory(Path.GetDirectoryName(executablePath)!),
                "IShellLinkW.SetWorkingDirectory");
            ThrowIfFailed(link.SetDescription("ReminNote"), "IShellLinkW.SetDescription");

            propertyStore = (IPropertyStore)linkObject;
            value = PropVariant.FromString(applicationUserModelId);
            var propertyKey = ApplicationUserModelIdKey;
            ThrowIfFailed(
                propertyStore.SetValue(ref propertyKey, ref value),
                "IPropertyStore.SetValue(PKEY_AppUserModel_ID)");
            ThrowIfFailed(propertyStore.Commit(), "IPropertyStore.Commit");

            // Persist only after the property store has committed the AUMID.
            // Saving first creates a valid-looking .lnk but leaves the
            // subsequently assigned property out of the file that a fresh
            // ShellLink instance reads during verification.
            ((IPersistFile)linkObject).Save(shortcutPath, true);
        }
        finally
        {
            value.Dispose();
            ReleaseCom(propertyStore);
            ReleaseCom(linkObject);
        }
    }

    private static bool VerifyShortcut(
        string shortcutPath,
        string executablePath,
        string applicationUserModelId)
    {
        if (!IsRegularFile(shortcutPath))
        {
            return false;
        }

        object? linkObject = null;
        IPropertyStore? propertyStore = null;
        PropVariant value = default;
        try
        {
            linkObject = new ShellLink();
            var persistFile = (IPersistFile)linkObject;
            persistFile.Load(shortcutPath, 0);
            var link = (IShellLinkW)linkObject;
            var target = new StringBuilder(32_768);
            ThrowIfFailed(link.GetPath(target, target.Capacity, IntPtr.Zero, 0), "IShellLinkW.GetPath");
            if (!Path.GetFullPath(target.ToString()).Equals(executablePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            propertyStore = (IPropertyStore)linkObject;
            var propertyKey = ApplicationUserModelIdKey;
            ThrowIfFailed(
                propertyStore.GetValue(ref propertyKey, out value),
                "IPropertyStore.GetValue(PKEY_AppUserModel_ID)");
            var registeredId = value.ReadString();
            return string.Equals(registeredId, applicationUserModelId, StringComparison.Ordinal);
        }
        finally
        {
            value.Dispose();
            ReleaseCom(propertyStore);
            ReleaseCom(linkObject);
        }
    }

    private static bool IsRegularDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static bool IsRegularFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId { get; } = formatId;
        public uint PropertyId { get; } = propertyId;
    }

    // PROPVARIANT is 24 bytes on win-x64: an eight-byte header followed by a
    // 16-byte union (for example DECIMAL and counted array members). The
    // previous two-field declaration was only 16 bytes, so IPropertyStore
    // could write past the managed buffer during GetValue/PropVariantClear.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(2)]
        private ushort Reserved1;

        [FieldOffset(4)]
        private ushort Reserved2;

        [FieldOffset(6)]
        private ushort Reserved3;

        [FieldOffset(8)]
        public IntPtr Pointer;

        // Keep the complete native union size even though this integration
        // only uses the LPWStr arm. These bytes are intentionally zeroed by
        // default construction and are never interpreted by managed code.
        [FieldOffset(16)]
        private long UnionPadding;

        public static PropVariant FromString(string value) => new()
        {
            VariantType = VariantTypeLpWStr,
            Pointer = Marshal.StringToCoTaskMemUni(value)
        };

        public string? ReadString() => VariantType is VariantTypeBstr or VariantTypeLpWStr
            ? Marshal.PtrToStringUni(Pointer)
            : null;

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                _ = PropVariantClear(ref this);
                Pointer = IntPtr.Zero;
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out] StringBuilder path, int maxPath, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr idList);
        [PreserveSig] int SetIDList(IntPtr idList);
        [PreserveSig] int GetDescription([Out] StringBuilder name, int maxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([Out] StringBuilder path, int maxPath);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        [PreserveSig] int GetArguments([Out] StringBuilder arguments, int maxArguments);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int showCommand);
        [PreserveSig] int SetShowCmd(int showCommand);
        [PreserveSig] int GetIconLocation([Out] StringBuilder path, int maxPath, out int index);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private const uint ShellChangeAssociationChanged = 0x08000000;
    private const uint ShellChangeNotifyIdList = 0x0000;

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);
}
