using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using QuazalWV;

namespace GRODedicatedServerWV
{
    public partial class Form1 : Form
    {
        // Map name -> bigfile key (hex string)
        private readonly Dictionary<string, string> mapKeyDictionary = new Dictionary<string, string>()
        {
            { "Metro", "0D80B43C" },
            { "Chertanovo", "DE139C36" },
            { "Oil Rig", "4D13CD5C" },
            { "Rooftop", "FD700758" }
        };

        public Form1()
        {
            InitializeComponent();
            Log.logFileName = "dslog.txt";
            Log.ClearLog();
            Log.box = rtb1;
            // The DS's own database.sqlite is empty (0 bytes). Open the BACKEND's live DB read-only so the
            // spawn can look up the player's loadout + each weapon's components. Read-only = safe concurrent
            // reads while the backend writes; falls back to the local DB if that path can't be opened.
            try
            {
                string backendDb = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    @"..\..\..\..\GROBackendWV\bin\x86\Release\database.sqlite"));
                DBHelper.Init("Data Source=" + backendDb + ";Read Only=True;BusyTimeout=3000");
            }
            catch { DBHelper.Init(); }
            toolStripComboBox1.SelectedIndex = 0;

            // populate map combo box
            mapComboBox.Items.Clear();
            foreach (var kv in mapKeyDictionary)
                mapComboBox.Items.Add(kv.Key);
            // default to Oil Rig if present
            int oilIndex = mapComboBox.Items.IndexOf("Oil Rig");
            if (oilIndex >= 0)
            {
                mapComboBox.SelectedIndex = oilIndex;
                toolStripTextBox2.Text = mapKeyDictionary["Oil Rig"];
            }
            else if (mapComboBox.Items.Count > 0)
            {
                mapComboBox.SelectedIndex = 0;
                toolStripTextBox2.Text = mapKeyDictionary[mapComboBox.Items[0].ToString()];
            }
        }

        // Auto-start overload: launching with -autostart (or /autostart) on the command line
        // fires the Start button once the window is shown (uses the default map key in the textbox).
        public Form1(string[] args) : this()
        {
            if (args != null && args.Any(a =>
                    a.Equals("-autostart", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/autostart", StringComparison.OrdinalIgnoreCase)))
                this.Shown += Form1_AutoStart;
        }

        private void Form1_AutoStart(object sender, EventArgs e)
        {
            this.Shown -= Form1_AutoStart; // one-shot
            toolStripButton1_Click(this, EventArgs.Empty);
        }

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            string mapKeyHex = null;

            if (mapComboBox.SelectedIndex >= 0 && mapComboBox.SelectedItem != null)
            {
                string selectedMap = mapComboBox.SelectedItem.ToString();
                mapKeyDictionary.TryGetValue(selectedMap, out mapKeyHex);
            }

            // fallback to manual textbox if combo didn't provide a key
            if (string.IsNullOrEmpty(mapKeyHex))
                mapKeyHex = toolStripTextBox2.Text.Trim();

            uint mapKey;
            try
            {
                mapKey = Convert.ToUInt32(mapKeyHex, 16);
            }
            catch
            {
                MessageBox.Show("Invalid map key: " + mapKeyHex, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SessionInfosParameter.defaultMapKey = mapKey;
            Log.WriteLine(1, "Using mapkey = 0x" + mapKey.ToString("X8"), Color.Red);
            timer1.Enabled = true;
            UDPDedicatedServer.Start();
            toolStripTextBox2.Enabled =
            mapComboBox.Enabled =
            toolStripButton1.Enabled = false;
            toolStripButton2.Enabled = true;

        }

        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            timer1.Enabled = false;
            UDPDedicatedServer.Stop();
            toolStripTextBox2.Enabled =
            mapComboBox.Enabled =
            toolStripButton1.Enabled = true;
            toolStripButton2.Enabled = false;
        }

        private void toolStripButton6_Click(object sender, EventArgs e)
        {
            rtb1.Text = "";
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            NotificationQuene.Update();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            UDPDedicatedServer.Stop();
        }

        private void toolStripComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (toolStripComboBox1.SelectedIndex)
            {
                default:
                case 0:
                    Log.MinPriority = 1;
                    break;
                case 1:
                    Log.MinPriority = 2;
                    break;
                case 2:
                    Log.MinPriority = 5;
                    break;
                case 3:
                    Log.MinPriority = 10;
                    break;
            }
        }

        private void mapComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (mapComboBox.SelectedIndex >= 0 && mapComboBox.SelectedItem != null)
            {
                string selectedMap = mapComboBox.SelectedItem.ToString();
                if (mapKeyDictionary.TryGetValue(selectedMap, out string mapKeyHex))
                {
                    toolStripTextBox2.Text = mapKeyHex;
                    Log.WriteLine(1, "Map selected: " + selectedMap + " (0x" + mapKeyHex + ")", Color.Blue);
                }
            }
        }

        private void toolStripButton3_Click(object sender, EventArgs e)
        {
            uint u = Convert.ToUInt32(toolStripTextBox1.Text.Trim(), 16);
            MessageBox.Show(new DupObj(u).getDesc());
        }

        private void toolStripButton4_Click(object sender, EventArgs e)
        {
            listBox1.Items.Clear();
            foreach (DupObj obj in DO_Session.DupObjs)
                listBox1.Items.Add(obj.getDesc());
        }

        private void listBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            rtb2.Text = "";
            int n = listBox1.SelectedIndex;
            if (n < 0 || n >= DO_Session.DupObjs.Count)
                return;
            if (DO_Session.DupObjs[n].Payload != null)
                rtb2.Text = DO_Session.DupObjs[n].Payload.getDesc();
            else
                rtb2.Text = "No Payload";
        }

        private void toolStripButton9_Click(object sender, EventArgs e)
        {
            Log.enablePacketLogging = toolStripButton9.Checked;
        }
    }
}
