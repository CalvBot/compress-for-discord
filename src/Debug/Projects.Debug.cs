namespace CompressForDiscord;

// Test code: only debug builds compile this folder (see the project file).

static partial class Projects
{
    /// <summary>
    /// Test runs never touch the real list of recent projects: CFD_PROJECTS names the folder to use, and a run with
    /// CFD_TEST but no folder of its own gets a throwaway one
    /// </summary>
    static partial void TestFolder(ref string folder)
    {
        if (Environment.GetEnvironmentVariable("CFD_PROJECTS") is { Length: > 0 } dir) folder = dir;
        else if (Environment.GetEnvironmentVariable("CFD_TEST") is { Length: > 0 }) folder = Path.Combine(Paths.Work, "test-projects");
    }
}
