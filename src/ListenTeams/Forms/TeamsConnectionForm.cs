using ListenTeams.Platform;

namespace ListenTeams.Forms;

/// <summary>Explains and checks the connection to the Teams local API (exact mute state).</summary>
internal sealed class TeamsConnectionForm : Form
{
    private readonly CallCoordinator _coordinator;
    private readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(3, 3, 3, 10) };
    private readonly Button _pair = new() { Text = "Demander l'autorisation à Teams", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public TeamsConnectionForm(CallCoordinator coordinator)
    {
        _coordinator = coordinator;
        Text = "ListenTeams — Connexion à Teams";
        Icon = TrayIcons.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(14);

        var explanation = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Text =
                "Pour ne jamais enregistrer votre voix quand votre micro est coupé dans Teams, ListenTeams lit l'état " +
                "du micro grâce à l'API locale de Teams (rien ne passe par Internet).\n\n" +
                "1. Dans Teams : Paramètres → Confidentialité → « Gérer l'API » (API d'application tierce), puis activez « Activer l'API ».\n" +
                "2. Rejoignez un appel ou une réunion : Teams vous demande d'autoriser « ListenTeams ». Acceptez.\n" +
                "   (Si la demande n'apparaît pas, cliquez sur le bouton ci-dessous pendant l'appel.)\n\n" +
                "Sans cette API, ListenTeams lit le bouton micro de la fenêtre d'appel Teams, ce qui fonctionne dans la plupart des cas.",
        };

        var close = new Button { Text = "Fermer", AutoSize = true, DialogResult = DialogResult.OK };
        _pair.Click += async (_, _) =>
        {
            _pair.Enabled = false;
            bool sent = await _coordinator.RequestPairingAsync();
            MessageBox.Show(this,
                sent ? "Demande envoyée : acceptez-la dans la fenêtre de Teams." : "Teams n'a pas pu être contacté. Vérifiez que l'API est activée dans Teams.",
                "ListenTeams", MessageBoxButtons.OK, sent ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_pair);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(_status);
        layout.Controls.Add(explanation);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = close;

        _timer.Tick += (_, _) => UpdateStatus(_coordinator.Status);
        _timer.Start();
        UpdateStatus(_coordinator.Status);
    }

    private void UpdateStatus(CoordinatorStatus status)
    {
        var api = status.Api;
        (string text, Color color) = (api.Connected, status.ApiPaired) switch
        {
            (true, true) => ("✔ Connecté à Teams et autorisé", Color.SeaGreen),
            (true, false) => ("● API Teams détectée — autorisation à donner pendant un appel", Color.DarkOrange),
            _ when !status.TeamsRunning => ("○ Teams n'est pas lancé", SystemColors.GrayText),
            _ => ("○ API Teams non activée (étape 1 ci-dessous)", Color.Firebrick),
        };
        _status.Text = text;
        _status.ForeColor = color;
        _pair.Enabled = api.Connected && api.CanPair && !status.ApiPaired;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
