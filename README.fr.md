> 🇬🇧 [English version](README.md)

# Orkeon Studio

<p align="center">
  <img src="docs/assets/orkeon-studio.png" alt="Orkeon Studio : le caméléon forgeron à son enclume" width="280">
</p>

Orkeon Studio est la famille de frontaux graphiques et terminal au-dessus du framework d'agents [Orkeon](https://github.com/Orkeon/orkeon) : éditer et valider l'`appsettings.json` qu'écrit `orkeon init`, créer une équipe, puis choisir un équipage et le lancer en exécutant la CLI `orkeon`.

| Application | Lanceur | Plateforme | Rôle |
|---|---|---|---|
| `Orkeon.Studio.Wpf` | `orkeon-studio` | Windows | Application de bureau : réglages, équipes, exécutions, dans une seule fenêtre. |
| `Orkeon.Studio.Config` | `orkeon-studio-config` | Linux, macOS, Windows (terminal) | Éditeur de réglages Terminal.Gui. |
| `Orkeon.Studio.Run` | `orkeon-studio-run` | Linux, macOS, Windows (terminal) | Lanceur d'équipages Terminal.Gui. |

Les trois partagent `Orkeon.Studio.Core`, une bibliothèque sans interface. Voir [docs/fr/architecture/studio.md](docs/fr/architecture/studio.md).

## Studio pilote la CLI `orkeon`

Studio n'exécute jamais un équipage dans son propre processus : il lance la CLI `orkeon` et lit son flux d'événements. La CLI doit être installée, et Studio la cherche dans cet ordre : l'argument `--cli-dir` (application WPF seulement), à côté de l'exécutable de Studio, la variable d'environnement `ORKEON_CLI_DIR`, puis le `PATH`. Installez-la avec les [installeurs d'Orkeon](https://github.com/Orkeon/orkeon/blob/main/docs/fr/reference/publication-matrix.md) ou comme outil .NET, et vérifiez-la avec `orkeon doctor`.

## Installer

Téléchargez l'asset de votre plateforme depuis la page [Releases](https://github.com/Orkeon/orkeon-studio/releases) et vérifiez-le avec le fichier `SHA256SUMS` joint à côté (`sha256sum -c SHA256SUMS --ignore-missing`).

| Asset | Plateforme | Contenu |
|---|---|---|
| `orkeon-studio-<version>-win-x64.msi` | Windows x64 | Installeur par utilisateur : l'application de bureau dans `%LOCALAPPDATA%\Programs\Orkeon Studio`, un raccourci « Orkeon Studio » dans le menu Démarrer. |
| `orkeon-studio-<version>-win-x64.zip` | Windows x64 | La même application de bureau : extrayez-la n'importe où, lancez `bin\orkeon-studio.cmd`. |
| `orkeon-studio-<version>-linux-x64.tar.gz` | Linux x64 | Les deux applications terminal, `bin/orkeon-studio-config` et `bin/orkeon-studio-run` : extrayez l'archive et ajoutez `bin/` au `PATH`. |

Chaque application est autonome : aucun runtime .NET n'est nécessaire. Les paquets ne sont pas encore signés : Windows SmartScreen avertit au premier lancement.

**Prérequis : la CLI `orkeon` dans le `PATH`.** Studio ne l'embarque pas. Installez-la comme outil .NET, `dotnet tool install -g Orkeon.Scripting.Cli`, ou avec les [installeurs d'Orkeon](https://github.com/Orkeon/orkeon/releases), puis vérifiez-la avec `orkeon doctor`. Sous Windows, Studio lance `orkeon.exe` lui-même : l'outil .NET en place un dans le `PATH` ; après un installeur d'Orkeon, dont le `bin\` contient des lanceurs `.cmd`, réglez `ORKEON_CLI_DIR` sur son dossier `libexec\orkeon` (`%LOCALAPPDATA%\Programs\Orkeon\libexec\orkeon`).

Comment une release est construite : [docs/fr/reference/release.md](docs/fr/reference/release.md).

## Construire depuis les sources

Prérequis : le SDK .NET fixé dans `global.json`, et Python 3 pour les scripts de `scripts/`.

```bash
dotnet restore Orkeon.Studio.sln
dotnet build Orkeon.Studio.sln --no-restore
dotnet test Orkeon.Studio.sln --no-build --filter "Category!=Integration&Category!=Slow"
```

Studio consomme Orkeon par des paquets NuGet (`Orkeon`, `Orkeon.Constants.Cli`, `Orkeon.Cli.Abstractions`, `Orkeon.Cli.TerminalGui` ; les tests ajoutent `Orkeon.Tools` et `Orkeon.Scripting`). Leur version est la propriété `OrkeonVersion` de `Directory.Packages.props`. Ils viennent de nuget.org, comme tous les autres paquets (`nuget.config`).

Les tests de dérive d'`Orkeon.Studio.Core.Tests` comparent Studio à des fichiers qu'Orkeon livre sans les empaqueter (équipages d'exemple, réglages d'exemple de l'installeur, inventaire des outils, sources de la CLI). Ils les lisent dans un checkout d'Orkeon : renseignez `ORKEON_SOURCE_ROOT`, ou clonez Orkeon à côté de ce dépôt sous `../orkeon`.

Lancer une application depuis les sources :

```bash
dotnet run --project src/apps/Orkeon.Studio.Config
dotnet run --project src/apps/Orkeon.Studio.Run
dotnet run --project src/apps/Orkeon.Studio.Wpf      # Windows seulement
```

## Installer depuis les sources

`scripts/install-from-source.sh` (Linux, macOS) et `scripts/install-from-source.ps1` (Windows, PowerShell 7) installent Studio depuis ce clone, à côté d'une CLI Orkeon. Avec `--with-orkeon` (`-WithOrkeon`), ils installent d'abord la CLI depuis le checkout d'Orkeon (`--orkeon-src`, sinon `ORKEON_SOURCE_ROOT`, sinon `../orkeon`) avec le script `install-from-source` de ce checkout.

```bash
./scripts/install-from-source.sh --with-orkeon            # la CLI, puis Studio, sous ~/.local
./scripts/install-from-source.sh                          # Studio seul, à côté d'une CLI installée
./scripts/install-from-source.sh --dry-run                # prérequis, versions, commandes
./scripts/install-from-source.sh --uninstall              # retire le Studio de ce script, garde la CLI
```

```powershell
pwsh -File .\scripts\install-from-source.ps1 -WithOrkeon  # Windows : la CLI, puis Studio
```

Le script empaquette Orkeon dans `orkeon-feed/` à une version datée par le dernier commit du checkout d'Orkeon (voir `scripts/resolve-version.sh`) et construit Studio contre ces paquets : un nouveau commit d'Orkeon donne une nouvelle version, jamais une copie périmée du cache NuGet. Il refuse d'empaqueter un checkout d'Orkeon qui a des modifications non commitées, ou de réutiliser des paquets de même version empaquetés depuis un autre commit. Cette construction lit un `nuget.config` qu'écrit le script, qui résout `Orkeon.*` depuis `orkeon-feed/` ; celui du dépôt ne lit que nuget.org. Studio s'installe dans l'installation de la CLI (`lib/orkeon/libexec`, ou `libexec\` de `%LOCALAPPDATA%\Programs\Orkeon`), où il trouve la CLI en premier. L'installeur de la CLI remplace ce dossier en entier : après avoir réinstallé la CLI, relancez le script.

## Contribuer

Voir [CONTRIBUTING.fr.md](CONTRIBUTING.fr.md), le [code de conduite](CODE_OF_CONDUCT.fr.md) et la [politique de sécurité](SECURITY.fr.md). Les contributions sont acceptées sous le [CLA](CLA.fr.md).

## Licence

MIT : voir [LICENSE.md](LICENSE.md) et [NOTICE.md](NOTICE.md).
