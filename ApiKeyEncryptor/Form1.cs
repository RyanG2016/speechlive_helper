using System;
using System.Drawing;
using System.Windows.Forms;
using EncryptionService;

namespace ApiKeyEncryptor
{
    public partial class Form1 : Form
    {
        // Initialize controls in the constructor
        private Label lblKeyStatus = new();
        private TextBox txtPlainApiKey = new();
        private TextBox txtEncryptedApiKey = new();
        private Button btnEncrypt = new();
        private Button btnCopy = new();
        private Button btnGenerateKey = new();
        private Button btnOpenSecretsFolder = new();

        private string _secretsFilePath;

        public Form1()
        {
            InitializeComponent();
            _secretsFilePath = EncryptionHelper.GetDefaultSecretsPath();
            InitializeCustomComponents();
            CheckSecretsFile();
        }

        private void InitializeCustomComponents()
        {
            this.Text = "API Key Encryptor";
            this.Size = new Size(600, 380);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            // Configure the key status label
            lblKeyStatus.Location = new Point(20, 15);
            lblKeyStatus.Size = new Size(540, 40);
            lblKeyStatus.Text = "Checking encryption key status...";

            // Configure the generate key button
            btnGenerateKey.Location = new Point(20, 55);
            btnGenerateKey.Size = new Size(260, 30);
            btnGenerateKey.Text = "Generate New Encryption Key";
            btnGenerateKey.Click += BtnGenerateKey_Click;

            // Configure the open folder button
            btnOpenSecretsFolder.Location = new Point(300, 55);
            btnOpenSecretsFolder.Size = new Size(260, 30);
            btnOpenSecretsFolder.Text = "Open secrets.json Folder";
            btnOpenSecretsFolder.Click += BtnOpenSecretsFolder_Click;

            // Configure the plain API key textbox
            txtPlainApiKey.Location = new Point(20, 100);
            txtPlainApiKey.Size = new Size(540, 60);
            txtPlainApiKey.Multiline = true;
            txtPlainApiKey.ScrollBars = ScrollBars.Vertical;
            txtPlainApiKey.PlaceholderText = "Enter your SpeechLive API key here...";

            // Configure the encrypt button
            btnEncrypt.Location = new Point(20, 170);
            btnEncrypt.Size = new Size(540, 40);
            btnEncrypt.Text = "Encrypt API Key";
            btnEncrypt.Click += BtnEncrypt_Click;

            // Configure the encrypted API key textbox
            txtEncryptedApiKey.Location = new Point(20, 220);
            txtEncryptedApiKey.Size = new Size(540, 60);
            txtEncryptedApiKey.Multiline = true;
            txtEncryptedApiKey.ScrollBars = ScrollBars.Vertical;
            txtEncryptedApiKey.ReadOnly = true;
            txtEncryptedApiKey.PlaceholderText = "Encrypted API key will appear here...";

            // Configure the copy button
            btnCopy.Location = new Point(20, 290);
            btnCopy.Size = new Size(540, 40);
            btnCopy.Text = "Copy to Clipboard";
            btnCopy.Click += BtnCopy_Click;

            // Add controls to the form
            this.Controls.AddRange(new Control[] {
                lblKeyStatus,
                btnGenerateKey,
                btnOpenSecretsFolder,
                txtPlainApiKey,
                btnEncrypt,
                txtEncryptedApiKey,
                btnCopy
            });
        }

        private void CheckSecretsFile()
        {
            var existingKey = EncryptionHelper.LoadSecretsFile(_secretsFilePath);

            if (existingKey != null)
            {
                lblKeyStatus.Text = $"Encryption key loaded from: {_secretsFilePath}";
                lblKeyStatus.ForeColor = Color.Green;
                btnEncrypt.Enabled = true;
                btnGenerateKey.Text = "Regenerate Encryption Key";
            }
            else
            {
                lblKeyStatus.Text = "No encryption key found. Generate a new key or copy an existing secrets.json to this folder.";
                lblKeyStatus.ForeColor = Color.Red;
                btnEncrypt.Enabled = false;
                btnGenerateKey.Text = "Generate New Encryption Key";
            }
        }

        private void BtnGenerateKey_Click(object? sender, EventArgs e)
        {
            var existingKey = EncryptionHelper.LoadSecretsFile(_secretsFilePath);

            if (existingKey != null)
            {
                var result = MessageBox.Show(
                    "An encryption key already exists. Generating a new key will:\n\n" +
                    "- Make all previously encrypted API keys INVALID\n" +
                    "- Require you to re-encrypt all API keys\n" +
                    "- Require updating secrets.json on all deployed machines\n\n" +
                    "Are you sure you want to generate a new key?",
                    "Warning: Key Already Exists",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.Yes)
                    return;
            }

            try
            {
                string newKey = EncryptionHelper.GenerateNewKey();
                EncryptionHelper.SaveSecretsFile(newKey, _secretsFilePath);
                EncryptionHelper.ClearCachedKey(); // Clear cache so new key is used

                MessageBox.Show(
                    $"New encryption key generated and saved to:\n{_secretsFilePath}\n\n" +
                    "IMPORTANT: Copy this secrets.json file to:\n" +
                    "C:\\ProgramData\\SpeechLive Helper\\\n\n" +
                    "on each machine where the SpeechLive Upload Helper service is installed.",
                    "Key Generated Successfully",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                CheckSecretsFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error generating encryption key: {ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void BtnOpenSecretsFolder_Click(object? sender, EventArgs e)
        {
            try
            {
                string folderPath = Path.GetDirectoryName(_secretsFilePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                System.Diagnostics.Process.Start("explorer.exe", folderPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error opening folder: {ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void BtnEncrypt_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtPlainApiKey.Text))
            {
                MessageBox.Show("Please enter an API key to encrypt.", "Input Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                string encryptedKey = EncryptionHelper.EncryptApiKey(txtPlainApiKey.Text);
                txtEncryptedApiKey.Text = encryptedKey;
            }
            catch (FileNotFoundException ex)
            {
                MessageBox.Show(
                    $"Encryption key not found.\n\n{ex.Message}",
                    "Encryption Key Missing",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error encrypting API key: {ex.Message}", "Encryption Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnCopy_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtEncryptedApiKey.Text))
            {
                MessageBox.Show("No encrypted API key to copy.", "No Data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Clipboard.SetText(txtEncryptedApiKey.Text);
                MessageBox.Show("Encrypted API key copied to clipboard!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error copying to clipboard: {ex.Message}", "Copy Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
