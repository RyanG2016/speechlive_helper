using System;
using System.Windows.Forms;
using EncryptionService;

namespace ApiKeyEncryptor
{
    public partial class Form1 : Form
    {
        // Initialize controls in the constructor
        private TextBox txtPlainApiKey = new();
        private TextBox txtEncryptedApiKey = new();
        private Button btnEncrypt = new();
        private Button btnCopy = new();

        public Form1()
        {
            InitializeComponent();
            InitializeCustomComponents();
        }

        private void InitializeCustomComponents()
        {
            this.Text = "API Key Encryptor";
            this.Size = new System.Drawing.Size(600, 300);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            // Configure the plain API key textbox
            txtPlainApiKey.Location = new System.Drawing.Point(20, 20);
            txtPlainApiKey.Size = new System.Drawing.Size(540, 60);
            txtPlainApiKey.Multiline = true;
            txtPlainApiKey.ScrollBars = ScrollBars.Vertical;
            txtPlainApiKey.PlaceholderText = "Enter your API key here...";

            // Configure the encrypt button
            btnEncrypt.Location = new System.Drawing.Point(20, 90);
            btnEncrypt.Size = new System.Drawing.Size(540, 40);
            btnEncrypt.Text = "Encrypt API Key";
            btnEncrypt.Click += BtnEncrypt_Click;

            // Configure the encrypted API key textbox
            txtEncryptedApiKey.Location = new System.Drawing.Point(20, 140);
            txtEncryptedApiKey.Size = new System.Drawing.Size(540, 60);
            txtEncryptedApiKey.Multiline = true;
            txtEncryptedApiKey.ScrollBars = ScrollBars.Vertical;
            txtEncryptedApiKey.ReadOnly = true;
            txtEncryptedApiKey.PlaceholderText = "Encrypted API key will appear here...";

            // Configure the copy button
            btnCopy.Location = new System.Drawing.Point(20, 210);
            btnCopy.Size = new System.Drawing.Size(540, 40);
            btnCopy.Text = "Copy to Clipboard";
            btnCopy.Click += BtnCopy_Click;

            // Add controls to the form
            this.Controls.AddRange(new Control[] { txtPlainApiKey, btnEncrypt, txtEncryptedApiKey, btnCopy });
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
