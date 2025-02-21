using System.Diagnostics;

namespace SpeechLiveUploader.Win
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }



        private void Form1_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                Hide();
                notifyIcon1.Visible = true;
                notifyIcon1.ShowBalloonTip(10000);
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            this.notifyIcon1.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
            this.notifyIcon1.ContextMenuStrip.Items.Add("Open Logs", null, this.MenuLogs_Click);
        }
        void MenuLogs_Click(object sender, EventArgs e)
        {
            string WorkingFolderPath = @"C:\\ProgramData\SpeechLive Helper\Working\";
            string LogsFolderPath = @"C:\\ProgramData\SpeechLive Helper\Logs\";
            if (!Directory.Exists(WorkingFolderPath))
                Directory.CreateDirectory(WorkingFolderPath);

            if (!Directory.Exists(LogsFolderPath))
                Directory.CreateDirectory(LogsFolderPath);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
            {
                FileName = LogsFolderPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }

        private void button1_Click(object sender, EventArgs e)
        {
            string WorkingFolderPath = @"C:\\ProgramData\SpeechLive Helper\Working\";
            string LogsFolderPath = @"C:\\ProgramData\SpeechLive Helper\Logs\";
            if (!Directory.Exists(WorkingFolderPath))
                Directory.CreateDirectory(WorkingFolderPath);

            if (!Directory.Exists(LogsFolderPath))
                Directory.CreateDirectory(LogsFolderPath);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
            {
                FileName = LogsFolderPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }

        private void button2_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
        }

        private void notifyIcon1_DoubleClick_1(object sender, EventArgs e)
        {
            Show();
            this.WindowState = FormWindowState.Normal;
            notifyIcon1.Visible = false;
        }
    }
}
