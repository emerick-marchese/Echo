# Politique de confidentialité d'Echo — Echo privacy policy

*Dernière mise à jour : 1er novembre 2026.*

Echo est un logiciel libre qui fonctionne sur votre PC. Il n'a ni compte, ni serveur, ni publicité, ni statistiques d'utilisation : son auteur ne reçoit aucune donnée.

## Ce qu'Echo enregistre, et où

- Les enregistrements (audio, transcription, synthèse, compte rendu, voix reconnues) sont stockés **uniquement sur votre PC**, dans le dossier choisi (par défaut `Documents\Echo`). Vous pouvez les supprimer à tout moment depuis Echo ou l'Explorateur.
- Les paramètres sont dans `%APPDATA%\Echo`. La clé API Anthropic, le secret de votre application Discord et l'autorisation donnée par Discord y sont chiffrés pour votre compte Windows.
- Si vous connectez Echo à Discord, Echo lit, pendant vos appels, le salon vocal où vous êtes : le pseudo des participants, qui parle et l'état de votre micro. Ces informations sont enregistrées avec l'appel (`discord.json`) pour nommer les participants dans la transcription. Echo ne lit pas vos messages, vos serveurs ni vos amis.
- Seuls sont enregistrés le son de l'application d'appel choisie (Teams ou Discord) et votre micro quand il est actif dans cette application. La reconnaissance des voix calcule des « empreintes vocales » sur le PC ; elles restent dans le dossier de l'enregistrement.

## Connexions réseau

Echo ne se connecte à Internet que dans ces cas :

| Quand | Vers | Ce qui est envoyé |
|---|---|---|
| Installation, ou premier besoin si l'installation n'a pas pu le faire | Hugging Face (modèle de transcription Whisper), GitHub (modèle de reconnaissance des voix) | Une simple demande de téléchargement |
| Installation, si le composant manque | Microsoft (Edge WebView2) | Une simple demande de téléchargement |
| Après un appel, **seulement si vous avez choisi Claude et saisi votre clé API** | Anthropic (API Claude) | Le texte de la transcription, pour rédiger la synthèse. Jamais l'audio. Traitement selon la [politique de confidentialité d'Anthropic](https://www.anthropic.com/legal/privacy). |
| Après un appel, si vous avez choisi Ollama | Votre propre serveur Ollama (par défaut sur ce PC) | Le texte de la transcription |
| Pendant que Teams est ouvert | L'API locale de Teams, sur ce PC (`127.0.0.1`) | Rien ne sort du PC |
| Pendant que Discord est ouvert, **seulement si vous avez connecté Echo à Discord** | L'application Discord, sur ce PC (canal local `discord-ipc`) | Rien ne sort du PC |
| Quand vous connectez Echo à Discord, puis environ une fois par semaine | Discord (`discord.com/api/oauth2/token`) | L'identifiant et le secret de **votre** application Discord, avec le code d'autorisation (ou le jeton de renouvellement), pour obtenir l'accès que vous avez accordé. Aucune donnée d'appel. |
| Liens que vous ouvrez (console Anthropic, Ollama…) | Votre navigateur | — |

## Enregistrer des personnes

Vous êtes responsable de l'usage que vous faites d'Echo. Informez les participants qu'un appel est enregistré : c'est une obligation légale dans la plupart des pays (en France, RGPD et article 226-1 du Code pénal).

## Contact

Questions : [ouvrez une discussion ou un ticket sur GitHub](https://github.com/emerick-marchese/Echo/issues).

---

**English summary.** Echo runs entirely on your PC and has no account, server, ads or telemetry; its author receives no data. Recordings, transcripts, summaries and voice prints stay in the folder you chose. Echo connects to the Internet only to download its speech and voice models (Hugging Face, GitHub) and, if missing, Microsoft Edge WebView2; and, only if you select Claude and enter your own API key, to send the transcript text (never audio) to Anthropic for the summary. With Ollama, the transcript goes to your own Ollama server. The Teams API used for the mute state is local (`127.0.0.1`). If you connect Echo to Discord (with your own Discord application), Echo talks to the Discord app on your PC to read the voice channel you are in (participants' names, who is speaking, your mute state), and contacts `discord.com` only to exchange and renew the authorization you granted. Please inform participants that a call is recorded.
