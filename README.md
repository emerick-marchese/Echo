# Echo

<img src="assets/echo.png" alt="" width="96" align="right">

**Echo** enregistre vos appels **Microsoft Teams** et **Discord** sous Windows, dans une application moderne où tout se gère :

1. choisissez **Teams** ou **Discord** ;
2. cliquez sur **Démarrer l'enregistrement** ;
3. Echo enregistre **uniquement** le son de l'application choisie et **votre micro, seulement quand il est actif dans cette application** : si vous êtes en muet (ou en sourdine dans Discord), votre voix n'est pas enregistrée ;
4. pendant l'appel, Echo montre **qui parle en temps réel** : vous, et chaque autre participant reconnu à sa voix (« Intervenant 1 », « Intervenant 2 »…, que vous pouvez nommer d'un clic) ;
5. à l'arrêt, Echo produit le **fichier audio**, la **transcription** (chaque phrase attribuée à vous ou à l'intervenant qui l'a dite) et une **synthèse** (résumé, points clés, décisions, actions, questions ouvertes, déroulé), consultables directement dans l'application.

Options : démarrage automatique dès qu'un appel commence, arrêt automatique à la fin de l'appel, thème sombre ou clair.

## Installation

1. Téléchargez **`Echo-Setup.exe`** depuis l'onglet *Releases* du dépôt.
2. Lancez-le : aucun droit administrateur n'est nécessaire et rien d'autre n'est à installer. L'application embarque son environnement .NET. L'installateur télécharge le modèle de transcription choisi et, s'il manque, le composant Microsoft Edge WebView2 qui affiche la fenêtre.

   | Modèle | Taille | Pour qui |
   |---|---|---|
   | Précis (large-v3-turbo) — **recommandé** | 547 Mo | PC récent ; meilleure transcription |
   | Équilibré (small) | 181 Mo | PC modeste |
   | Rapide (base) | 57 Mo | Très petite configuration, qualité réduite |

3. Si **ListenTeams** (l'ancien nom d'Echo) était installé, il est remplacé : paramètres, clé API et modèle déjà téléchargé sont repris.

Configuration requise : Windows 10 version 2004 ou plus récent / Windows 11, 64 bits, applications de bureau Teams (nouvelle ou classique) et/ou Discord (stable, PTB, Canary).

## L'application

| Écran | Contenu |
|---|---|
| **Enregistrer** | Choix Teams / Discord (avec leur état : fermé, ouvert, en appel), gros bouton Démarrer / Arrêter, chronomètre, niveaux sonores en direct (participants / vous), état de votre micro (« Micro actif — enregistré » / « Micro coupé — non enregistré »), **qui parle en ce moment** et temps de parole de chacun (cliquez sur une voix pour lui donner un nom), options de démarrage et d'arrêt automatiques |
| **Enregistrements** | Tous vos appels : recherche, filtre Teams / Discord, état du traitement. Pour chaque appel : lecteur audio, intervenants (renommables, avec « Refaire la synthèse avec ces noms »), synthèse, transcription cliquable (un clic sur un horodatage relance l'audio à ce moment), renommer, relancer la transcription, ouvrir le dossier, supprimer (corbeille) |
| **Paramètres** | Thème, nom affiché, dossier, démarrage avec Windows, qualité audio, micro, annulation d'écho, modèle et langue de transcription, carte graphique, vocabulaire, moteur de synthèse, connexion à Teams |

Fermer la fenêtre ne coupe pas Echo : il reste près de l'horloge (et continue d'enregistrer si un enregistrement est en cours). Clic droit sur l'icône : ouvrir Echo, démarrer / arrêter l'enregistrement, quitter.

## Réglages conseillés

### Synthèse

*Paramètres → Synthèse* :

- **Claude (Anthropic)**, par défaut : la meilleure qualité. Collez votre clé API ([console Anthropic](https://console.anthropic.com/settings/keys)) ; elle est chiffrée pour votre compte Windows. Seul le **texte** de la transcription est envoyé ; l'audio ne quitte jamais le PC.
- **Ollama**, 100 % local : installez [Ollama](https://ollama.com) puis un modèle (par exemple `ollama pull mistral-nemo`).
- **Aucune** : transcription seule.

### Teams : état exact du micro (recommandé)

Dans Teams : **Paramètres → Confidentialité → Gérer l'API** → **Activer l'API**. Pendant votre prochain appel enregistré, Teams demande d'autoriser **Echo** : acceptez (ou *Paramètres → Teams → Demander l'autorisation* pendant l'appel). Sans cette API, Echo lit le bouton micro de la fenêtre d'appel Teams.

### Discord : rien à configurer

Echo lit les boutons **Rendre muet** et **Mettre en sourdine** du panneau utilisateur de Discord. Gardez la fenêtre Discord ouverte pendant l'appel (elle peut être en arrière-plan ou réduite, mais pas fermée dans la zone de notification). Le **push-to-talk** n'est pas détecté : utilisez le muet de Discord pour exclure un passage.

Quand Echo ne parvient pas à connaître l'état de votre micro, votre voix **n'est pas enregistrée** par défaut et l'application vous prévient (réglable dans *Paramètres → Audio*).

## Fichiers produits

Chaque appel a son dossier, par exemple `Documents\Echo\2026-10-31 18h02 - Raid du jeudi\` :

```
appel.opus           audio de l'appel (Opus mono, ≈ 11 Mo/heure en qualité recommandée)
transcription.txt    [00:01:23] Moi : …
synthese.md          compte rendu en Markdown
compte-rendu.html    synthèse + lecteur audio + transcription, lisible dans n'importe quel navigateur
infos.json           application, date, durée, état du traitement
appel.env, transcription.json, voix.json   données internes (niveaux sonores, transcription et voix pour l'application)
```

## Comment ça marche

```
Teams ────► état : API locale Teams (ws://127.0.0.1:8124) ou bouton micro de la fenêtre ; appel détecté via l'usage du micro
Discord ──► état : boutons « Rendre muet » / « Mettre en sourdine » (UI Automation) ; appel détecté via l'usage du micro

Démarrer l'enregistrement (application choisie)
  son de l'app ─► capture WASAPI par processus (arbre ms-teams.exe ou Discord.exe) ─┐
  micro ────────► capture WASAPI (mode communications + annulation d'écho)          ├─► mixage aligné ─► Opus
                   └─ porte « muet » pilotée par l'état du micro dans l'app ────────┘     (1 s de marge)

Après l'arrêt
  Opus ─► 48→16 kHz ─► passages parlés uniquement ─► Whisper (local) ─► Moi / Participants
       ─► synthèse (Claude ou Ollama) ─► fichiers + affichage dans Echo
```

- **Son de l'application uniquement** : la capture Windows *par processus* isole Teams ou Discord ; ni la musique, ni les notifications, ni les autres applications.
- **Micro « tel que l'application l'entend »** : le mixage a une seconde de retard sur le temps réel, ce qui permet d'appliquer chaque passage en muet au bon endroit (avec 250 ms de marge). L'API Teams signale les changements instantanément ; les boutons des fenêtres sont lus 5 fois par seconde.
- **Qui parle ?** Votre voix est reconnue par votre micro (après la porte « muet »). Les autres participants sont reconnus à leur **empreinte vocale** (modèle CAM++, 28 Mo, calculé sur le PC) : en direct toutes les 0,4 s sur la dernière 1,6 s de son, puis, après l'appel, phrase par phrase avec un regroupement plus précis. Les noms donnés pendant l'appel sont repris dans la transcription. Ça marche pareil sur Teams et Discord, sans configuration ; deux voix très proches peuvent parfois être confondues.
- **Transcription fiable** : seuls les passages parlés vont à Whisper (pas de phrases inventées dans les silences) ; GPU (Vulkan) si disponible.
- **Robuste** : l'audio est écrit au fil de l'eau ; si le PC s'éteint pendant un appel, l'enregistrement est conservé et traité au démarrage suivant. Les enregistrements de moins de 8 secondes sont ignorés.

## Limites connues

- Un seul appel est enregistré à la fois. Les intervenants sont reconnus à la voix, pas par leur compte Teams / Discord : leurs vrais noms ne sont connus que si vous les donnez (ou si la synthèse les déduit de la conversation).
- Un **casque** donne le meilleur résultat ; sans casque, l'annulation d'écho de Windows retire le son des haut-parleurs si le matériel la prend en charge.
- Teams et Discord dans le navigateur ne sont pas pris en charge.
- La transcription d'une heure prend de quelques minutes (GPU) à environ une heure (processeur seul, modèle « Précis ») ; elle tourne en arrière-plan.

## Confidentialité et cadre légal

Tout est stocké sur votre PC. Seul le texte de la transcription est envoyé à Anthropic si vous choisissez Claude. **Informez les participants que l'appel est enregistré** : c'est une obligation légale dans la plupart des pays (en France, RGPD et article 226-1 du Code pénal). Détails : [politique de confidentialité](PRIVACY.md).

## Licence et signature du code

Echo est un logiciel libre sous [licence MIT](LICENSE).

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org) — voir la [politique de signature du code](CODE_SIGNING.md) (demande en cours : tant qu'elle n'est pas acceptée, les fichiers publiés ne sont pas signés et Windows peut afficher « Windows a protégé votre ordinateur » : cliquez sur *Informations complémentaires* puis *Exécuter quand même*).

## Développement

Prérequis : SDK .NET 10.

```bash
dotnet test tests/Echo.Tests
```

```bash
dotnet publish src/Echo/Echo.csproj -c Release -r win-x64 --self-contained true -o publish
```

```bash
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=2.0.0 installer\Echo.iss
```

L'interface (`src/Echo/ui`) s'ouvre aussi dans un navigateur, avec des données de démonstration : servez le dossier (`python -m http.server` dans `src/Echo/ui`) puis ouvrez `index.html` (paramètres d'URL : `?page=recordings`, `?recording=1`, `?theme=light`…).

Le workflow `.github/workflows/build.yml` teste, compile et produit `Echo-Setup.exe` à chaque push. Pour publier une *Release* : onglet *Actions* → **Build** → *Run workflow* avec la version (par exemple `2.1.0`), ou poussez un tag `v2.1.0`.

| Dossier | Contenu |
|---|---|
| `src/Echo.Core` | Logique testée : applications prises en charge, mixage et porte « muet », Opus, détection d'appel, API Teams, boutons Discord, transcription Whisper, synthèse, bibliothèque des enregistrements, compte rendu HTML |
| `src/Echo` | Application Windows : fenêtre WebView2 et pont vers l'interface, moteur d'enregistrement, captures WASAPI (NAudio), UI Automation, icône de notification |
| `src/Echo/ui` | Interface (HTML, CSS, JavaScript sans dépendance) |
| `tests/Echo.Tests` | Tests unitaires et de bout en bout |
| `installer` | Script Inno Setup de `Echo-Setup.exe` |
| `tools/make_icon.py` | Génère le logo (`assets/echo.ico`, `assets/echo.png`) |

Bibliothèques : [NAudio](https://github.com/naudio/NAudio), [Concentus](https://github.com/lostromb/concentus), [Whisper.net](https://github.com/sandrohanea/whisper.net), [SDK Anthropic C#](https://github.com/anthropics/anthropic-sdk-csharp), [Markdig](https://github.com/xoofx/markdig), [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/).
