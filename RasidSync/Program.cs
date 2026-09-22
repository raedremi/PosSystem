namespace RasidSync
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // يمنع فتح نسختين؛ وجود نسختين قد يكرر إرسال أو استقبال نفس الدورة.
            using var singleInstance = new Mutex(
                initiallyOwned: true,
                name: @"Local\RasidSync.SingleInstance",
                createdNew: out bool createdNew);

            if (!createdNew)
            {
                MessageBox.Show(
                    "برنامج المزامنة يعمل مسبقًا بجانب الساعة.",
                    "Rasid Sync",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
