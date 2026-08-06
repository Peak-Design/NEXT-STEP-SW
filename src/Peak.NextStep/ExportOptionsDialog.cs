using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Peak.NextStep
{
    /// <summary>
    /// Export options. Deliberately small: two decisions the user actually has
    /// to make, each with the trade-off written next to it.
    ///
    /// Two things about hosting a WinForms dialog inside SolidWorks, both of
    /// which cost a round of "the text looks wrong":
    ///
    ///   * Text rendering. Application.UseCompatibleTextRendering defaults to
    ///     TRUE unless the host calls SetCompatibleTextRenderingDefault(false),
    ///     which a native C++ host like SolidWorks never does. Every control
    ///     then draws through GDI+, which antialiases in greyscale instead of
    ///     using ClearType, and the result looks soft and slightly smeared next
    ///     to every other dialog on screen. Each control has to opt out
    ///     individually -- setting it once on the form does nothing.
    ///
    ///   * Layout. Hand-placed pixel bounds looked right on the machine they
    ///     were written on and clipped the help text everywhere else: the
    ///     message-box font is whatever the theme says it is, and GDI+ measures
    ///     wider than GDI anyway. Everything here auto-sizes instead.
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

        // Engineering material defaults OFF: some importers collapse every
        // appearance into one material per CAD material when it is present,
        // which hides the colour work this add-in exists to do.
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
                + "when it writes STEP. This addon restores them.",
                SystemColors.ControlText);
            intro.Margin = new Padding(0, 0, 0, 10);

            _deInstance = Check("De-instance components that carry an override",
                                deInstanceDefault);
            var deInstanceHelp = Prose(
                "On: each overridden occurrence becomes its own part with its own "
                + "colour. Duplicates geometry, but respects the SolidWorks appearance "
                + "hierarchy in every reader.\n"
                + "Off: geometry stays fully instanced and each occurrence's colour is "
                + "written as occurrence level styling instead. Compact and correct per "
                + "ISO 10303-46, but only readers that implement occurrence styling "
                + "show it.",
                SystemColors.GrayText);
            deInstanceHelp.Margin = new Padding(20, 0, 0, 12);

            _engineeringMaterial = Check("Include engineering material (name and density)",
                                         engineeringMaterialDefault);
            var materialHelp = Prose(
                "Writes the CAD material SolidWorks itself never exports, for tools that "
                + "read material and density.\n"
                + "With de-instancing on, each distinct appearance gets its own numbered "
                + "variant of the name, so readers that build one material per name keep "
                + "the colours apart.",
                SystemColors.GrayText);
            materialHelp.Margin = new Padding(20, 0, 0, 12);

            _includeHidden = Check("Include hidden components", includeHiddenDefault);
            var hiddenHelp = Prose(
                "SolidWorks leaves hidden components out of a STEP file. On, they are "
                + "shown for the duration of the export and hidden again afterwards, so "
                + "they are written like any other component.\n"
                + "Suppressed components are never exported either way, because resolving "
                + "one would rebuild the assembly.",
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
        /// Show the dialog owned by the SolidWorks window that invoked the
        /// command, so it centres on SolidWorks and behaves as a modal child of
        /// it rather than a stray top-level window. Returns false on cancel.
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
