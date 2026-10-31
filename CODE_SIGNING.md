# Code signing policy — Politique de signature du code

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> **Statut / Status** : demande déposée auprès de SignPath Foundation. Tant qu'elle n'est pas acceptée, les fichiers publiés ne sont pas signés. — Application submitted to SignPath Foundation; until it is accepted, released files are not signed.

## Team roles — Rôles

- Committers and reviewers — Auteurs et relecteurs : [emerick-marchese](https://github.com/emerick-marchese)
- Approvers — Approbateurs : [emerick-marchese](https://github.com/emerick-marchese)

## What is signed — Ce qui est signé

For each release, GitHub Actions builds Echo from this repository ([`.github/workflows/build.yml`](.github/workflows/build.yml)) and submits to SignPath:

- `Echo.exe`, `Echo.dll`, `Echo.Core.dll` — Echo's own programs ([`.signpath/artifact-configurations/binaries.xml`](.signpath/artifact-configurations/binaries.xml));
- `Echo-Setup.exe` — the installer ([`.signpath/artifact-configurations/installer.xml`](.signpath/artifact-configurations/installer.xml)).

Each signing request is approved by hand by an approver. Third-party libraries keep their own signatures (or none) and are not re-signed.

Pour chaque version, GitHub Actions compile Echo à partir de ce dépôt et envoie à SignPath les programmes d'Echo et l'installateur ci-dessus. Chaque demande de signature est approuvée à la main. Les bibliothèques tierces ne sont pas re-signées.

## Privacy policy — Confidentialité

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. See [PRIVACY.md](PRIVACY.md) for the full list of network connections.

Ce programme ne transmet aucune information à d'autres systèmes en réseau, sauf à la demande expresse de l'utilisateur ou de la personne qui l'installe ou l'utilise. La liste complète des connexions réseau figure dans [PRIVACY.md](PRIVACY.md).
