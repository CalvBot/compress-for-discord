namespace CompressForDiscord;

/// <summary>The optional desktop shortcut (the installer adds the Start menu one and removes this on uninstall)</summary>
static class Shortcut
{
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Compress for Discord.lnk");

    public static bool OnDesktop => File.Exists(DesktopLink);

    public static void SetOnDesktop(bool on)
    {
        if (!on) { File.Delete(DesktopLink); return; }
        var exe = Environment.ProcessPath!;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(DesktopLink);
        link.TargetPath = exe;
        link.WorkingDirectory = Path.GetDirectoryName(exe);
        link.IconLocation = exe + ",0";
        link.Description = "Compress videos to fit Discord's upload limit";
        link.Save();
    }
}
