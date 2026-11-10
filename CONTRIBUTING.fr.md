> 🇬🇧 [English version](CONTRIBUTING.md)

# Contribuer à Orkeon Studio

## Code de conduite

Ce projet et tous ceux qui y participent sont régis par notre [code de conduite](CODE_OF_CONDUCT.fr.md). En participant, vous vous engagez à le respecter.

## Signaler un bogue, proposer une amélioration

Les deux passent par une issue GitHub. Donnez un titre clair, les étapes exactes, ce que vous avez observé et ce que vous attendiez, et la sortie d'`orkeon doctor` quand le problème touche une exécution. Un problème du moteur, de la CLI ou de la configuration qu'elle lit relève du [dépôt Orkeon](https://github.com/Orkeon/orkeon/issues).

## Pull requests

1. Créez votre branche depuis `main` : `feature/<slug>`, `fix/<slug>` ou `chore/<slug>`. Rien n'est commité directement sur `main`.
2. Ajoutez des tests pour le code que vous ajoutez, et gardez la suite au vert.
3. Mettez la documentation à jour, en anglais et dans son miroir français sous `docs/fr/`.
4. À votre première pull request, acceptez le [contrat de licence des contributeurs](CLA.fr.md).

## Environnement de développement

Prérequis : le SDK .NET fixé dans `global.json`, Python 3, et la CLI `orkeon` pour lancer les applications (voir le [README](README.fr.md)).

```bash
dotnet restore Orkeon.Studio.sln     # une fois, puis seulement quand une référence de paquet change
dotnet build Orkeon.Studio.sln --no-restore                 # la CI construit en -c Release -warnaserror : zéro avertissement
dotnet test Orkeon.Studio.sln --no-build --filter "Category!=Integration&Category!=Slow"   # ce que lance la CI
```

L'exécuteur de tests est **Microsoft.Testing.Platform, pas VSTest** (`global.json`). Un `--filter` qui ne retient aucun test dans un module est une erreur (code 8) : ajoutez `-- --ignore-exit-code 8` à un filtre sur la solution que la plupart des modules ne satisfont pas. Les options propres à VSTest (`--collect`, `--logger`, `--blame`) sont refusées.

Studio consomme Orkeon par des paquets NuGet, à la version que fixe `OrkeonVersion` dans `Directory.Packages.props`. Les tests de dérive d'`Orkeon.Studio.Core.Tests` lisent des fichiers d'un checkout d'Orkeon : renseignez `ORKEON_SOURCE_ROOT`, ou clonez Orkeon sous `../orkeon`.

Le flux local est le dossier `orkeon-feed/` à la racine du dépôt, ignoré par git (le chemin que nomme `nuget.config`, relatif à lui-même). Remplissez-le depuis un checkout d'Orkeon avec `scripts/pack-orkeon-feed.sh <checkout-orkeon>` (il empaquette chaque paquet que Studio restaure à la version que fixe `OrkeonVersion`), puis restaurez. La CI empaquette le commit d'Orkeon que nomme `.github/orkeon-source-ref` : déplacez cette référence pour construire contre un Orkeon plus récent. Ne retéléchargez jamais ce que le cache `packages/` contient déjà.

## Structure du projet

| Chemin | Contenu |
|---|---|
| `src/apps/Orkeon.Studio.Core` | Cœur sans interface : modèle de réglages, préréglages, montages, validation, lancement de la CLI. |
| `src/apps/Orkeon.Studio.Config` | Éditeur de réglages Terminal.Gui. |
| `src/apps/Orkeon.Studio.Run` | Lanceur d'équipages Terminal.Gui. |
| `src/apps/Orkeon.Studio.Wpf` | Application de bureau WPF (`net10.0-windows`). |
| `tests/apps/Orkeon.Studio.*.Tests` | Un projet de tests par application. `Orkeon.Studio.Wpf.Tests` compile les ViewModels WPF par lien et tourne sous Linux. |
| `tests/shared/Orkeon.Studio.Tests.Shared` | Fixtures partagées (espaces de noms `Orkeon.Tests.Shared.*`, copiées d'Orkeon). |
| `docs/` | Architecture, avec son miroir français dans `docs/fr/`. |
| `scripts/` | Vérifications du dépôt. |

## Règles imposées par le build

* **Pas de `System.IO` ni de `System.Console` directs.** Les fichiers passent par `IFileSystemService` ; l'analyseur `Orkeon.Compliance.Vfs` fait échouer le build sinon. Le code d'amorçage est marqué `// EXCEPTION-BOOTSTRAP`, la détection système `// OUT-OF-SCOPE`, et un symbole qui doit toucher le disque ou la console porte `[SuppressVfsCompliance("raison")]` ou `[SuppressConsoleCompliance("raison")]`. Voir [conformité VFS](https://github.com/Orkeon/orkeon/blob/main/docs/fr/architecture/vfs-compliance.md) et [conformité console](https://github.com/Orkeon/orkeon/blob/main/docs/fr/architecture/console-compliance.md).
* **`Orkeon.Domain.Task` masque `System.Threading.Tasks.Task`**, et `Orkeon.Application` masque l'`Application` de WPF : dans un fichier qui voit les deux, écrivez le nom complet.
* **Zéro avertissement** : la CI construit avec `-warnaserror` sous le jeu complet d'analyseurs (`AnalysisMode=All`).

## Les commentaires sont en anglais, sans accent

`scripts/check-comment-accents.py` fait échouer la CI sur un commentaire qui porte une lettre accentuée ou du français. L'interface de Studio est traduite : le piège est un commentaire qui cite un libellé d'interface. Traduisez le libellé, n'en retirez pas les accents. Les chaînes affichées à l'utilisateur gardent leurs accents.

## Tests

* xUnit et ses assertions natives seulement : pas de framework de mocks (Moq, NSubstitute, FakeItEasy) ni de bibliothèque d'assertions fluentes (FluentAssertions, Shouldly, Verify).
* Les doublures de test sont des classes écrites à la main, nommées d'après l'interface qu'elles remplacent, préfixées `Mock`, `Fake` ou `Stub`, dans un dossier `Doubles/` (ou `Fakes/`) du projet de tests qui les utilise, avec des champs simples pour configurer les réponses et inspecter les appels.
* Les tests tiennent quand plusieurs passes tournent en même temps : un délai que le test ne doit pas atteindre est un garde-fou (`Polling.DefaultTimeout`), et aucun test unitaire ne borne une durée.
* Un test qui demande Docker, un service réseau ou plusieurs minutes porte `[Trait("Category", "Integration")]` ou `[Trait("Category", "Slow")]`.

## Scripts et commits

* Un script `.sh`, ou tout script à shebang fait pour être lancé directement, est commité exécutable (mode `100755`) : `git update-index --chmod=+x <chemin>`. Le contrôle `file-modes.yml` lit le mode dans l'index git.
* Les messages de commit sont en anglais et suivent la forme `type(portée): résumé` (`feat`, `fix`, `docs`, `test`, `chore`).

## Licence

En contribuant, vous acceptez que vos contributions soient placées sous licence MIT ([LICENSE.md](LICENSE.md)) et acceptez le [CLA](CLA.fr.md).
