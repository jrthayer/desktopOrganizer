namespace FenceTool;

internal static class Program
{
    private const string MutexName = "Global\\FenceTool-SingleInstance-9f2b8e3a";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Fence Tool is already running (check the system tray).", "Fence Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
