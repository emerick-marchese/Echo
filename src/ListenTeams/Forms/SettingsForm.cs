using ListenTeams.Core.Settings;
using ListenTeams.Core.Transcription;
using ListenTeams.Platform;
using NAudio.CoreAudioApi;

namespace ListenTeams.Forms;

internal sealed class SettingsForm : Form
{
    private readonly SettingsStore _store;

    private readonly CheckBox _recordTeams = new() { Text = "Microsoft Teams", AutoSize = true };
    private readonly CheckBox _recordDiscord = new() { Text = "Discord", AutoSize = true };
    private readonly TextBox _outputFolder = new() { Width = 340 };
    private readonly TextBox _myName = new() { Width = 200 };
    private readonly TextBox _othersName = new() { Width = 200 };
    private readonly CheckBox _autoStart = new() { Text = "Lancer ListenTeams à l'ouverture de session Windows", AutoSize = true };
    private readonly CheckBox _notifyStart = new() { Text = "Me prévenir quand un enregistrement démarre", AutoSize = true };

    private readonly ComboBox _quality = Combo(330);
    private readonly ComboBox _microphone = Combo(330);
    private readonly CheckBox _echo = new() { Text = "Supprimer l'écho des haut-parleurs (annulation d'écho Windows)", AutoSize = true };
    private readonly CheckBox _micUnknown = new() { Text = "Enregistrer mon micro même si ListenTeams ne sait pas s'il est coupé dans Teams", AutoSize = true };

    private readonly ComboBox _model = Combo(330);
    private readonly ComboBox _language = Combo(200);
    private readonly CheckBox _gpu = new() { Text = "Utiliser la carte graphique quand c'est possible (plus rapide)", AutoSize = true };
    private readonly TextBox _vocabulary = new() { Width = 380, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical };

    private readonly ComboBox _provider = Combo(330);
    private readonly TextBox _apiKey = new() { Width = 330, UseSystemPasswordChar = true };
    private readonly TextBox _claudeModel = new() { Width = 200 };
    private readonly TextBox _ollamaUrl = new() { Width = 250 };
    private readonly TextBox _ollamaModel = new() { Width = 200 };

    private static readonly (int Bitrate, string Label)[] Qualities =
    [
        (16000, "Léger — 16 kb/s (≈ 7 Mo par heure)"),
        (24000, "Recommandé — 24 kb/s (≈ 11 Mo par heure)"),
        (32000, "Haute — 32 kb/s (≈ 14 Mo par heure)"),
    ];

    private static readonly (string Code, string Label)[] Languages =
    [
        ("fr", "Français"), ("en", "English"), ("auto", "Détection automatique"),
        ("de", "Deutsch"), ("es", "Español"), ("it", "Italiano"), ("nl", "Nederlands"), ("pt", "Português"),
    ];

    private readonly List<string> _microphoneIds = [string.Empty];
    private readonly List<string> _modelFiles = [string.Empty];

    public SettingsForm(SettingsStore store)
    {
        _store = store;
        Text = "ListenTeams — Paramètres";
        Icon = TrayIcons.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9f);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(Page("Général", BuildGeneral()));
        tabs.TabPages.Add(Page("Audio", BuildAudio()));
        tabs.TabPages.Add(Page("Transcription", BuildTranscription()));
        tabs.TabPages.Add(Page("Synthèse", BuildSummary()));

        // The form is modeless: buttons must close it themselves.
        var save = new Button { Text = "Enregistrer", AutoSize = true };
        var cancel = new Button { Text = "Annuler", AutoSize = true };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        AcceptButton = save;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        tabs.Size = new Size(620, 400);
        root.Controls.Add(tabs);
        root.Controls.Add(buttons);
        Controls.Add(root);

        LoadValues();
    }

    private Control BuildGeneral()
    {
        var browse = new Button { Text = "Parcourir…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _outputFolder.Text, Description = "Dossier des enregistrements" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _outputFolder.Text = dialog.SelectedPath;
        };
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        folderRow.Controls.Add(_outputFolder);
        folderRow.Controls.Add(browse);

        var appsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        appsRow.Controls.Add(_recordTeams);
        appsRow.Controls.Add(_recordDiscord);

        return Grid(
            ("Enregistrer les appels", appsRow),
            ("Dossier des enregistrements", folderRow),
            ("Votre nom dans la transcription", _myName),
            ("Nom des autres participants", _othersName),
            (null, _autoStart),
            (null, _notifyStart),
            (null, Note("Chaque appel est rangé dans son propre dossier : audio (appel.opus), transcription, synthèse et compte-rendu.html (à ouvrir dans le navigateur, avec lecteur audio).")));
    }

    private Control BuildAudio()
    {
        foreach (var (_, label) in Qualities) _quality.Items.Add(label);

        _microphone.Items.Add("Micro par défaut de Windows (communications)");
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                _microphoneIds.Add(device.ID);
                _microphone.Items.Add(device.FriendlyName);
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Liste des micros indisponible : {ex.Message}");
        }

        return Grid(
            ("Qualité de l'enregistrement", _quality),
            ("Microphone", _microphone),
            (null, Note("Choisissez le même micro que dans Teams ou Discord (Paramètres → Appareils / Voix et vidéo).")),
            (null, _echo),
            (null, _micUnknown),
            (null, Note("Seul le son de l'application d'appel est capturé (pas les autres applications). Votre micro n'est enregistré que lorsqu'il est actif dans Teams ou Discord. Un casque donne le meilleur résultat.")));
    }

    private Control BuildTranscription()
    {
        var folders = ModelCatalog.SearchFolders(AppPaths.AppFolder, AppPaths.DataFolder).ToList();
        _model.Items.Add("Automatique — meilleur modèle installé");
        foreach (var model in ModelCatalog.Models)
        {
            bool installed = folders.Any(f => File.Exists(Path.Combine(f, model.FileName)));
            _modelFiles.Add(model.FileName);
            _model.Items.Add($"{model.Label}{(installed ? " — installé" : $" — à télécharger ({model.SizeMb} Mo)")}");
        }
        foreach (var (_, label) in Languages) _language.Items.Add(label);

        return Grid(
            ("Modèle de reconnaissance", _model),
            ("Langue des appels", _language),
            (null, _gpu),
            ("Vocabulaire (noms, sigles…)", _vocabulary),
            (null, Note("La transcription est faite sur ce PC (Whisper), après l'appel. Le vocabulaire aide à bien orthographier noms de personnes, produits et sigles.")));
    }

    private Control BuildSummary()
    {
        _provider.Items.AddRange(["Claude (Anthropic) — meilleure qualité", "Ollama — 100 % local", "Aucune (transcription seule)"]);
        _provider.SelectedIndexChanged += (_, _) => UpdateProviderFields();

        var keyLink = new LinkLabel { Text = "Obtenir une clé API Anthropic", AutoSize = true };
        keyLink.LinkClicked += (_, _) => AppPaths.Open("https://console.anthropic.com/settings/keys");

        return Grid(
            ("Moteur de synthèse", _provider),
            ("Clé API Anthropic", _apiKey),
            (null, keyLink),
            ("Modèle Claude", _claudeModel),
            ("Adresse d'Ollama", _ollamaUrl),
            ("Modèle Ollama", _ollamaModel),
            (null, Note("Avec Claude, seul le texte de la transcription est envoyé à Anthropic pour rédiger la synthèse ; l'audio ne quitte jamais le PC. Avec Ollama (ollama.com), rien ne quitte le PC.")));
    }

    private void LoadValues()
    {
        var s = _store.Current;
        _recordTeams.Checked = s.RecordTeams;
        _recordDiscord.Checked = s.RecordDiscord;
        _outputFolder.Text = s.OutputFolder;
        _myName.Text = s.MyName;
        _othersName.Text = s.OthersName;
        _autoStart.Checked = AutoStart.IsEnabled;
        _notifyStart.Checked = s.NotifyOnCallStart;

        int quality = Array.FindIndex(Qualities, q => q.Bitrate == s.OpusBitrate);
        _quality.SelectedIndex = quality >= 0 ? quality : 1;
        _microphone.SelectedIndex = Math.Max(0, _microphoneIds.IndexOf(s.MicrophoneDeviceId));
        _echo.Checked = s.EchoCancellation;
        _micUnknown.Checked = s.RecordMicWhenMuteUnknown;

        _model.SelectedIndex = Math.Max(0, _modelFiles.FindIndex(f => f.Equals(s.WhisperModel, StringComparison.OrdinalIgnoreCase)));
        int language = Array.FindIndex(Languages, l => l.Code == s.Language);
        _language.SelectedIndex = language >= 0 ? language : 0;
        _gpu.Checked = s.UseGpu;
        _vocabulary.Text = s.Vocabulary;

        _provider.SelectedIndex = (int)s.SummaryProvider;
        _apiKey.PlaceholderText = string.IsNullOrEmpty(s.ClaudeApiKeyProtected) ? "sk-ant-…" : "Clé enregistrée (laisser vide pour la conserver)";
        _claudeModel.Text = s.ClaudeModel;
        _ollamaUrl.Text = s.OllamaUrl;
        _ollamaModel.Text = s.OllamaModel;
        UpdateProviderFields();
    }

    private void UpdateProviderFields()
    {
        bool claude = _provider.SelectedIndex == (int)SummaryProvider.Claude;
        bool ollama = _provider.SelectedIndex == (int)SummaryProvider.Ollama;
        _apiKey.Enabled = _claudeModel.Enabled = claude;
        _ollamaUrl.Enabled = _ollamaModel.Enabled = ollama;
    }

    private void Save()
    {
        string apiKey = _apiKey.Text.Trim();
        _store.Update(s =>
        {
            s.RecordTeams = _recordTeams.Checked;
            s.RecordDiscord = _recordDiscord.Checked;
            s.OutputFolder = string.IsNullOrWhiteSpace(_outputFolder.Text) ? AppSettings.DefaultOutputFolder : _outputFolder.Text.Trim();
            s.MyName = Fallback(_myName.Text, "Moi");
            s.OthersName = Fallback(_othersName.Text, "Participants");
            s.NotifyOnCallStart = _notifyStart.Checked;

            s.OpusBitrate = Qualities[Math.Max(0, _quality.SelectedIndex)].Bitrate;
            s.MicrophoneDeviceId = _microphoneIds[Math.Max(0, _microphone.SelectedIndex)];
            s.EchoCancellation = _echo.Checked;
            s.RecordMicWhenMuteUnknown = _micUnknown.Checked;

            s.WhisperModel = _modelFiles[Math.Max(0, _model.SelectedIndex)];
            s.Language = Languages[Math.Max(0, _language.SelectedIndex)].Code;
            s.UseGpu = _gpu.Checked;
            s.Vocabulary = _vocabulary.Text.Trim();

            s.SummaryProvider = (SummaryProvider)Math.Max(0, _provider.SelectedIndex);
            if (apiKey.Length > 0) _store.SetClaudeApiKey(s, apiKey);
            s.ClaudeModel = Fallback(_claudeModel.Text, "claude-opus-5-5");
            s.OllamaUrl = Fallback(_ollamaUrl.Text, "http://localhost:11434");
            s.OllamaModel = Fallback(_ollamaModel.Text, "mistral-nemo");
        });

        try
        {
            AutoStart.Set(_autoStart.Checked);
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Démarrage automatique non modifié : {ex.Message}");
        }
        Close();
    }

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static ComboBox Combo(int width) => new() { Width = width, DropDownStyle = ComboBoxStyle.DropDownList };

    private static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 8, 3, 3),
    };

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(10), UseVisualStyleBackColor = true };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    private static TableLayoutPanel Grid(params (string? Label, Control Control)[] rows)
    {
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, control) in rows)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label is null)
            {
                grid.Controls.Add(control, 0, row);
                grid.SetColumnSpan(control, 2);
            }
            else
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3) }, 0, row);
                grid.Controls.Add(control, 1, row);
            }
        }
        return grid;
    }
}
