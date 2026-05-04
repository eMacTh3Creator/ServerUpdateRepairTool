namespace WinUpdateRepairTool;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => MessageBox.Show(
            e.Exception.ToString(),
            "Unexpected error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        Application.Run(new Form1());
    }
}
