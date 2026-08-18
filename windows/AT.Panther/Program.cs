namespace ATPanther;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single-Instanz: Zweitstart blendet nur einen Hinweis ein
        using var mutex = new Mutex(true, @"Local\ATPanther_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "AT Panther läuft bereits und befindet sich im Tray-Bereich.",
                "AT Panther",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(new UI.MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Unerwarteter Fehler:\n" + ex.Message,
                "AT Panther",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
