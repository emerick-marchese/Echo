# ListenTeams

Enregistre automatiquement vos appels **Microsoft Teams** et/ou **Discord** sous Windows (vous choisissez lesquels), puis produit pour chaque appel :

- un **fichier audio** compact (`appel.opus`, environ 11 Mo par heure, parole d'excellente qualité) ;
- une **transcription** horodatée qui distingue *vous* et *les autres participants* (`transcription.txt`) ;
- une **synthèse** : résumé, points clés, décisions, actions à mener, questions ouvertes, déroulé (`synthese.md`) ;
- un **compte rendu** à ouvrir dans le navigateur (`compte-rendu.html`), avec lecteur audio intégré : un clic sur un horodatage relance l'appel à ce moment-là.

ListenTeams démarre avec Windows, se loge discrètement près de l'horloge, détecte Teams et Discord et l'arrivée ou la fin d'un appel, et enregistre **uniquement** :

- **le son de l'application d'appel** (les autres participants) : capture audio *par processus*, ni la musique, ni les notifications, ni les autres applications ;
- **votre micro, seulement quand il est actif dans Teams ou Discord** : si vous parlez alors que votre micro est coupé (ou que vous êtes en sourdine dans Discord), rien n'est enregistré.

## Installation

1. Téléchargez **`ListenTeams-Setup.exe`** :
   - version publiée : onglet *Releases* du dépôt ;
   - dernière version compilée : onglet *Actions* → workflow **Build** → dernier run → artefact `ListenTeams-Setup-…`.
2. Lancez-le. Aucun droit administrateur n'est nécessaire et rien d'autre n'est à installer : l'application embarque son environnement .NET, et l'installateur télécharge le modèle de transcription choisi :

   | Modèle | Taille | Pour qui |
   |---|---|---|
   | Précis (large-v3-turbo) — **recommandé** | 547 Mo | PC récent ; meilleure transcription |
   | Équilibré (small) | 181 Mo | PC modeste |
   | Rapide (base) | 57 Mo | Très petite configuration, qualité réduite |

   L'installateur demande aussi quelles applications enregistrer : **Teams**, **Discord** ou les deux.
3. À la fin, ListenTeams se lance et ouvre ses paramètres.

Le choix des applications se change à tout moment : clic droit sur l'icône → **Applications à enregistrer**, ou *Paramètres → Général*.

Configuration requise : Windows 10 version 2004 ou plus récent / Windows 11, 64 bits, application de bureau Teams (nouvelle ou classique) et/ou Discord (stable, PTB ou Canary).

## Réglages à faire une fois

### 1. Teams : autoriser ListenTeams à lire l'état du micro (recommandé)

C'est ce qui permet de savoir **exactement** quand votre micro est coupé dans Teams, et de détecter les réunions de façon fiable. Tout se passe en local, rien ne passe par Internet.

1. Dans Teams : **Paramètres → Confidentialité → Gérer l'API** (*API d'application tierce*) → activez **Activer l'API**.
2. Pendant votre prochain appel, Teams demande d'autoriser **ListenTeams** : acceptez.
   Si la demande n'apparaît pas : menu ListenTeams → **Connexion à Teams…** → *Demander l'autorisation à Teams* (pendant l'appel).

Sans cette API, ListenTeams lit le libellé du bouton micro dans la fenêtre d'appel Teams (« Désactiver le micro » / « Activer le micro »). Si aucun des deux n'est disponible, votre voix **n'est pas enregistrée** par défaut (réglable dans *Paramètres → Audio*) et une notification vous prévient.

### Discord : rien à configurer

- **Détection de l'appel** : ListenTeams voit quand Discord utilise le micro, ce qui est le cas pendant toute la durée d'un salon vocal ou d'un appel, même micro coupé.
- **Micro coupé** : ListenTeams lit les boutons **Rendre muet** et **Mettre en sourdine** du panneau utilisateur de Discord (en bas à gauche). Gardez la fenêtre Discord ouverte : elle peut rester en arrière-plan ou réduite, mais pas fermée dans la zone de notification. Sinon, votre voix n'est pas enregistrée par défaut et une notification vous prévient.
- **Push-to-talk** : si vous utilisez « Appuyer pour parler », Discord ne coupe pas le micro entre deux appuis ; ListenTeams enregistre donc votre voix tant que vous n'êtes pas en muet. Utilisez le muet de Discord pour exclure un passage.

### 2. Choisir le moteur de synthèse

*Menu ListenTeams → Paramètres → Synthèse* :

- **Claude (Anthropic)**, par défaut : la meilleure qualité de synthèse. Collez votre clé API ([console Anthropic](https://console.anthropic.com/settings/keys)) ; elle est chiffrée pour votre compte Windows (DPAPI). Seul le **texte** de la transcription est envoyé pour rédiger la synthèse ; l'audio ne quitte jamais le PC.
- **Ollama**, 100 % local : installez [Ollama](https://ollama.com) puis un modèle (par exemple `ollama pull mistral-nemo`). Rien ne quitte le PC.
- **Aucune** : transcription seule.

## Utilisation

Rien à faire : rejoignez un appel Teams ou un salon vocal Discord, l'icône passe au rouge et une notification confirme l'enregistrement. À la fin de l'appel, la transcription et la synthèse sont générées en arrière-plan, et une notification « Compte rendu prêt » ouvre le compte rendu d'un clic.

Menu de l'icône (clic droit) :

| Entrée | Rôle |
|---|---|
| État | En attente / enregistrement en cours (application, durée, état du micro) / traitement |
| Enregistrer maintenant / Arrêter | Démarrer ou arrêter à la main (un arrêt manuel vaut jusqu'à la fin de l'appel) |
| Ouvrir le dernier compte rendu, Appels récents | Accès direct aux comptes rendus |
| Ouvrir le dossier des enregistrements | `Documents\ListenTeams` par défaut |
| Applications à enregistrer | Cocher Microsoft Teams, Discord ou les deux |
| Connexion à Teams… | État et activation de l'API Teams |
| Paramètres… | Applications, dossier, qualité audio, micro, modèle et langue de transcription, vocabulaire, synthèse |
| Journaux | Fichiers de diagnostic |

Chaque appel a son dossier, par exemple `Documents\ListenTeams\2026-10-30 14h05 - Point hebdo\` :

```
appel.opus           audio de l'appel (Opus 24 kb/s mono, ≈ 11 Mo/heure)
appel.env            niveaux sonores (sert à savoir qui parle), ≈ 0,5 Mo/heure
transcription.txt    [00:01:23] Moi : …
synthese.md          compte rendu en Markdown
compte-rendu.html    synthèse + lecteur audio + transcription cliquable
infos.json           date, durée, état du traitement
```

`appel.opus` se lit avec le lecteur multimédia de Windows, VLC ou n'importe quel navigateur. Qualité au choix (*Paramètres → Audio*) : 16 kb/s (≈ 7 Mo/h), **24 kb/s (≈ 11 Mo/h, recommandé)**, 32 kb/s (≈ 14 Mo/h).

## Comment ça marche

```
Teams ────► détection d'appel ─┬─ API locale Teams (ws://127.0.0.1:8124) : en réunion, micro coupé
                               └─ Windows : Teams utilise le micro (registre « CapabilityAccessManager »)
Discord ──► détection d'appel ─── Windows : Discord utilise le micro
            micro coupé ───────── boutons « Rendre muet » / « Mettre en sourdine » (UI Automation)

Pendant l'appel (une application à la fois)
  son de l'app ─► capture WASAPI par processus (arbre ms-teams.exe ou Discord.exe) ─┐
  micro ────────► capture WASAPI (mode communications + annulation d'écho)          ├─► mixage aligné ─► Opus
                   └─ porte « muet » pilotée par l'état du micro dans l'app ────────┘     (1 s de marge)

Après l'appel
  Opus ─► 48→16 kHz ─► passages parlés uniquement ─► Whisper (local) ─► Moi / Participants
       ─► synthèse (Claude ou Ollama) ─► transcription.txt, synthese.md, compte-rendu.html
```

Points clés :

- **Son de l'application uniquement** : l'API Windows de capture par processus (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`) isole Teams ou Discord et leurs processus enfants.
- **Micro « tel que l'application l'entend »** : le mixage est fait avec une seconde de retard sur le temps réel, ce qui permet d'appliquer chaque changement « muet » au bon endroit, avec 250 ms de marge en arrière pour couvrir le délai de l'application. L'API Teams pousse les changements instantanément ; les boutons de la fenêtre (Teams sans API, Discord) sont lus 5 fois par seconde.
- **Une application à la fois** : si un appel Discord commence pendant un appel Teams enregistré (ou l'inverse), seul le premier est enregistré.
- **Qui parle ?** Le niveau sonore de votre micro (après la porte « muet ») et celui de l'application sont mémorisés toutes les 50 ms : chaque phrase est attribuée au côté le plus fort.
- **Transcription rapide et sans phrases inventées** : seuls les passages parlés sont envoyés à Whisper ; les silences, où Whisper a tendance à « halluciner » (« Sous-titres réalisés par… »), sont écartés, et ces phrases parasites sont filtrées. Le GPU est utilisé s'il est disponible (Vulkan), sinon le processeur.
- **Robuste** : le fichier audio est écrit au fil de l'eau ; si le PC s'éteint pendant un appel, l'enregistrement est conservé et traité au prochain démarrage. Les appels de moins de 8 secondes (test de micro, appel manqué) sont ignorés.

## Limites connues

- Les autres participants ne sont pas distingués entre eux (« Participants ») ; la synthèse utilise leurs noms quand ils sont prononcés.
- Utilisez de préférence un **casque** : sans casque, l'annulation d'écho de Windows retire le son des haut-parleurs du micro si votre matériel la prend en charge.
- Teams et Discord dans le navigateur ne sont pas pris en charge (applications de bureau uniquement).
- Discord : le muet est lu dans la fenêtre Discord (pas d'API officielle) ; si Discord change ses libellés ou si sa fenêtre est fermée dans la zone de notification, l'état du micro devient « inconnu » (votre voix n'est alors pas enregistrée, par défaut). Le push-to-talk n'est pas détecté.
- La transcription d'une heure d'appel prend de quelques minutes (avec GPU) à environ une heure (processeur seul, modèle « Précis ») ; elle tourne en arrière-plan.

## Confidentialité et cadre légal

Tout est stocké sur votre PC. Seul le texte de la transcription est envoyé à Anthropic si vous choisissez Claude pour la synthèse. **Informez les participants que l'appel est enregistré** : c'est une obligation légale dans la plupart des pays (en France, RGPD et article 226-1 du Code pénal).

## Développement

Prérequis : SDK .NET 10.

```bash
dotnet test tests/ListenTeams.Tests
```

```bash
dotnet publish src/ListenTeams/ListenTeams.csproj -c Release -r win-x64 --self-contained true -o publish
```

```bash
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0 installer\ListenTeams.iss
```

L'installateur est produit dans `installer\Output\ListenTeams-Setup.exe`. Le workflow GitHub Actions `.github/workflows/build.yml` fait tout cela à chaque push (artefact téléchargeable). Pour publier une *Release* avec l'installateur : onglet *Actions* → **Build** → *Run workflow* en indiquant la version (par exemple `1.1.0`), ou poussez un tag `v*` (`git tag v1.1.0 && git push origin v1.1.0`).

| Dossier | Contenu |
|---|---|
| `src/ListenTeams.Core` | Logique multiplateforme et testée : applications prises en charge (Teams, Discord), mixage et porte « muet », Opus, détection d'appel, client de l'API Teams, lecture des boutons Discord, découpage de la parole, transcription Whisper, synthèse (Claude / Ollama), compte rendu HTML |
| `src/ListenTeams` | Application Windows : icône de notification, captures WASAPI (NAudio), processus et fenêtres Teams/Discord, UI Automation, paramètres |
| `tests/ListenTeams.Tests` | Tests unitaires et de bout en bout (appel synthétique → transcription → synthèse → compte rendu) |
| `installer` | Script Inno Setup de `ListenTeams-Setup.exe` |
| `tools/make_icon.py` | Génère `assets/listenteams.ico` |

Bibliothèques : [NAudio](https://github.com/naudio/NAudio) (WASAPI), [Concentus](https://github.com/lostromb/concentus) (Opus), [Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp), [SDK Anthropic C#](https://github.com/anthropics/anthropic-sdk-csharp), [Markdig](https://github.com/xoofx/markdig).
