using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Peak.NextStep
{
    /// <summary>
    /// The export options. This dialog stays small on purpose. It shows only
    /// the decisions the user must make, and the cost of each one next to it.
    ///
    /// A WinForms dialog inside SolidWorks has two traps. Each one cost a round
    /// of "the text looks wrong".
    ///
    ///   * Text rendering. Application.UseCompatibleTextRendering is TRUE until
    ///     the host calls SetCompatibleTextRenderingDefault(false). A native C++
    ///     host such as SolidWorks never calls it. Every control then draws
    ///     through GDI+, which antialiases in greyscale instead of ClearType.
    ///     The text looks soft next to every other dialog on the screen. Each
    ///     control must refuse this by itself. A single setting on the form does
    ///     nothing.
    ///
    ///   * Layout. Pixel positions written by hand looked correct on one machine
    ///     and clipped the help text on all the others. The message box font is
    ///     whatever the theme selects, and GDI+ measures wider than GDI. Every
    ///     control here sizes itself instead.
    /// </summary>
    public sealed class ExportOptionsDialog : Form
    {
        private readonly CheckBox _deInstance;
        private readonly CheckBox _engineeringMaterial;
        private readonly CheckBox _includeHidden;

        public bool DeInstance => _deInstance.Checked;
        public bool EngineeringMaterial => _engineeringMaterial.Checked;
        public bool IncludeHidden => _includeHidden.Checked;

        /// <summary>Wrap width for prose.</summary>
        private const int TextWidth = 470;

        // Engineering material is OFF by default. Some importers merge every
        // appearance into one material for each CAD material when the file
        // holds one. That hides the colour work this add-in exists to do.
        public ExportOptionsDialog(bool deInstanceDefault = true,
                                   bool engineeringMaterialDefault = false,
                                   bool includeHiddenDefault = false)
        {
            Text = "Export STEP+";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = SystemFonts.MessageBoxFont;

            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
            };

            var intro = Prose(
                "SolidWorks flattens component and assembly level appearance overrides "
                + "when it writes STEP. This addon puts them back.",
                SystemColors.ControlText);
            intro.Margin = new Padding(0, 0, 0, 10);

            _deInstance = Check("De-instance components that carry an override",
                                deInstanceDefault);
            var deInstanceHelp = Prose(
                "On: each overridden occurrence becomes its own part with its own "
                + "colour. This duplicates geometry, but every reader then shows the "
                + "SolidWorks appearance hierarchy.\n"
                + "Off: the geometry stays fully instanced, and the colour of each "
                + "occurrence becomes occurrence level styling. This output is compact "
                + "and correct, but only readers that support occurrence styling show "
                + "it.",
                SystemColors.GrayText);
            deInstanceHelp.Margin = new Padding(20, 0, 0, 12);

            _engineeringMaterial = Check("Include engineering material (name and density)",
                                         engineeringMaterialDefault);
            var materialHelp = Prose(
                "Writes the CAD material that SolidWorks never exports, for tools that "
                + "read a material name and density.\n"
                + "With de-instancing on, each different appearance also gets its own "
                + "numbered variant of the name. Readers that build one material for "
                + "each name then keep the colours apart.",
                SystemColors.GrayText);
            materialHelp.Margin = new Padding(20, 0, 0, 12);

            _includeHidden = Check("Include hidden components", includeHiddenDefault);
            var hiddenHelp = Prose(
                "SolidWorks keeps hidden components out of a STEP file. When this "
                + "option is on, the addon shows them for the export and hides them "
                + "again afterwards.\n"
                + "Neither setting exports suppressed components, because SolidWorks "
                + "must rebuild the assembly to resolve one.",
                SystemColors.GrayText);
            hiddenHelp.Margin = new Padding(20, 0, 0, 14);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            var cancel = Push("Cancel", DialogResult.Cancel);
            var ok = Push("Export", DialogResult.OK);
            ok.Margin = new Padding(6, 3, 3, 3);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            root.Controls.Add(intro);
            root.Controls.Add(_deInstance);
            root.Controls.Add(deInstanceHelp);
            root.Controls.Add(_engineeringMaterial);
            root.Controls.Add(materialHelp);
            root.Controls.Add(_includeHidden);
            root.Controls.Add(hiddenHelp);
            root.Controls.Add(buttons);
            Controls.Add(root);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static Label Prose(string text, Color colour)
            => new Label
            {
                Text = text,
                ForeColor = colour,
                AutoSize = true,
                MaximumSize = new Size(TextWidth, 0),
                Margin = new Padding(0),
                UseCompatibleTextRendering = false,
            };

        private static CheckBox Check(string text, bool chequed)
            => new CheckBox
            {
                Text = text,
                Checked = chequed,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2),
                UseCompatibleTextRendering = false,
            };

        private static Button Push(string text, DialogResult result)
            => new Button
            {
                Text = text,
                DialogResult = result,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(84, 26),
                UseCompatibleTextRendering = false,
            };

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        /// <summary>
        /// Shows the dialog. The SolidWorks window that started the command
        /// owns it. The dialog therefore centres on SolidWorks and behaves as a
        /// modal child of it, not as a separate top level window. Returns false
        /// if the user cancels.
        /// </summary>
        public static bool Show(out bool deInstance, out bool engineeringMaterial,
                                out bool includeHidden)
        {
            deInstance = false;
            engineeringMaterial = false;
            includeHidden = false;

            IWin32Window owner = null;
            try
            {
                var hwnd = GetActiveWindow();
                if (hwnd != IntPtr.Zero) owner = new HostWindow(hwnd);
            }
            catch (Exception ex) { AddIn.Log("owner window unavailable: " + ex.Message); }

            using (var dlg = new ExportOptionsDialog())
            {
                var result = owner == null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
                if (result != DialogResult.OK) return false;
                deInstance = dlg.DeInstance;
                engineeringMaterial = dlg.EngineeringMaterial;
                includeHidden = dlg.IncludeHidden;
                return true;
            }
        }

        private sealed class HostWindow : IWin32Window
        {
            public HostWindow(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
