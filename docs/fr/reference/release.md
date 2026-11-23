> 🇬🇧 [English version](../../reference/release.md)

# Release

Orkeon Studio est publié depuis ce dépôt, avec sa propre version : `VersionPrefix` dans `src/Directory.Build.props`, la source unique de vérité, plus un élément `VersionSuffix` ajouté là pour une pré-version seulement. La CLI `orkeon` est publiée par [Orkeon](https://github.com/Orkeon/orkeon) et s'installe à part : Studio ne l'embarque jamais.

## Artefacts

| Asset | Construit par | Contenu |
|---|---|---|
| `orkeon-studio-<version>-win-x64.zip` | `scripts/package-studio.sh` | `bin\orkeon-studio.cmd`, `libexec\orkeon-studio\` (l'application WPF, `Orkeon.Studio.exe`). |
| `orkeon-studio-<version>-win-x64.msi` | `scripts/build-msi.ps1` | L'arborescence du zip, par utilisateur, dans `%LOCALAPPDATA%\Programs\Orkeon Studio`, plus un raccourci « Orkeon Studio » dans le menu Démarrer. Aucune entrée dans le `PATH`. |
| `orkeon-studio-<version>-linux-x64.tar.gz` | `scripts/package-studio.sh` | `bin/orkeon-studio-config`, `bin/orkeon-studio-run`, `libexec/<app>/` (fichiers identiques liés en dur, stockés une fois). |
| `SHA256SUMS` | `scripts/package-studio.sh` (archives), `scripts/build-msi.ps1` (la ligne du MSI, dans `SHA256SUMS.msi`), fusionnés par le job `release` | Une ligne par asset. |

Chaque archive contient aussi `README.md`, `LICENSE.md`, `NOTICE.md`, `THIRD-PARTY-NOTICES.md`, `VERSION` et, sous `licenses/<pack>/`, la licence et les notices des runtime packs .NET qu'elle embarque. Chaque application est publiée autonome (`--self-contained true`), non élaguée (`PublishTrimmed=false`) et pas en fichier unique, les options qu'Orkeon utilisait pour Studio. L'`UpgradeCode` du MSI (`installers/msi/Package.wxs`) est généré une fois et ne change jamais : c'est par lui qu'une mise à jour reconnaît un Orkeon Studio antérieur. La `ProductVersion` du MSI perd le suffixe ; la version complète reste dans le nom du fichier et dans le commentaire d'Ajout/Suppression de programmes.

## Le workflow

`.github/workflows/release.yml` s'exécute sur un tag `v*` (et sur `workflow_dispatch`, sans la publication) :

1. `build-test` : refuse un tag qui n'est pas `v` + la version des props, refuse un tag tant que Studio se construit contre un Orkeon non tagué (`OrkeonSourceRef` défini dans `Directory.Packages.props`, ou la source temporaire `orkeon-dev-feed` dans `nuget.config`), vérifie qu'aucun commentaire ne porte de lettre accentuée, clone le tag d'Orkeon `v<OrkeonVersion>` (superficiel, `--filter=blob:none` ; le commit que nomme `OrkeonSourceRef` quand elle est définie, comme le fait aussi `ci.yml`) pour les tests de dérive, puis construit avec `-warnaserror` et lance les tests, comme le job Linux de `ci.yml` ; le job Windows `build-wpf` de `ci.yml` n'a pas d'équivalent ici, l'application WPF étant compilée pour Windows par `package` et lancée sous Windows par `msi`.
2. `package` (Ubuntu) : `scripts/package-studio.sh` publie les deux RID depuis Linux (publication croisée) ; le projet WPF active `EnableWindowsTargeting`. Les étapes 3 et 4 tournent ensuite en parallèle, et `release` attend les deux.
3. `smoke-linux` (Ubuntu) : vérifie les archives avec `SHA256SUMS`, puis `scripts/smoke-studio-linux.sh` extrait l'archive et vérifie que les deux applications terminal répondent à `--version` sans terminal (sur un tag, avec la version du tag) et que l'application Windows n'y figure pas.
4. `msi` (Windows) : `scripts/smoke-studio-windows.ps1` lance `orkeon-studio.cmd --smoke-exit` sur le zip extrait (la fenêtre s'ouvre, s'affiche et se ferme avec le code 0), `scripts/build-msi.ps1` construit le MSI avec WiX v5, qui est ensuite installé (son entrée d'Ajout/Suppression de programmes et son raccourci du menu Démarrer vérifiés, `orkeon-studio.cmd --smoke-exit` relancé depuis le dossier d'installation) puis désinstallé (charge utile, raccourci et entrée d'Ajout/Suppression de programmes vérifiés absents).
5. `release` (tag seulement) : chaque asset est vérifié contre `SHA256SUMS`, puis joint à une release GitHub **brouillon** (un brouillon laissé par un run en échec est supprimé puis recréé ; une release déjà publiée fait échouer le job : taguez une nouvelle version), marquée prerelease quand la version a un suffixe. Le brouillon se publie à la main.

## Publier une release

1. Réglez `VersionPrefix` (et un `VersionSuffix` pour une pré-version) dans `src/Directory.Build.props` et déplacez la section `Unreleased` de `CHANGELOG.md` sous cette version, dans une pull request.
2. Une fois fusionnée, posez le tag `v<version>` sur le commit de fusion et poussez le tag.
3. Quand le workflow est vert, relisez la release brouillon et publiez-la.

## Empaqueter en local

```bash
./scripts/package-studio.sh --rids linux-x64      # artifacts/release/orkeon-studio-<version>-linux-x64.tar.gz
./scripts/smoke-studio-linux.sh artifacts/release/orkeon-studio-*-linux-x64.tar.gz
```

La première publication pour un RID restaure ses runtime packs. `--no-restore` saute cette restauration quand chaque projet a déjà été restauré avec le même `-r <rid>` et `-p:SelfContained=true`. Le MSI ne se construit que sous Windows : `pwsh scripts/build-msi.ps1 -StageDir <le dossier unique à la racine du zip extrait>`.
