> 🇬🇧 [English version](SECURITY.md)

# Politique de sécurité

## Versions prises en charge

Seule la dernière version publiée d'Orkeon Studio reçoit des correctifs de sécurité.

## Signaler une vulnérabilité

**N'ouvrez pas de ticket public pour une vulnérabilité de sécurité.**

Signalez les vulnérabilités via le [signalement privé de vulnérabilité GitHub](https://docs.github.com/fr/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) de ce dépôt (onglet « Security » → « Report a vulnerability »).

À inclure :

- Une description de la vulnérabilité et de son impact.
- Les étapes de reproduction (fichier de configuration ou équipe de démonstration si possible).
- L'application concernée (`orkeon-studio`, `orkeon-studio-config`, `orkeon-studio-run`) et sa plateforme.

Nous visons un accusé de réception sous **72 heures**, et un plan de correction ou un correctif sous **30 jours** pour les problèmes confirmés.

## Périmètre

Orkeon Studio est un front-end : il édite la configuration, lance la CLI `orkeon` installée séparément et lit sa sortie. Il n'exécute jamais une équipe dans son propre processus.

- Une faille de Studio lui-même (écriture de la configuration, stockage ou transmission des secrets, résolution de la CLI, traitement de la sortie de la CLI) relève de ce dépôt.
- Une faille du moteur, de la CLI, des outils ou des fournisseurs de LLM relève du [dépôt Orkeon](https://github.com/Orkeon/orkeon). Sa [politique de sécurité](https://github.com/Orkeon/orkeon/blob/main/SECURITY.fr.md) porte aussi le modèle de menace de l'exécution d'outils pilotée par un LLM et les recommandations de durcissement, qui valent pour toute équipe que Studio lance.

## Recommandations de durcissement

- Installez Studio depuis les fichiers de la Release GitHub et vérifiez-les avec le fichier `SHA256SUMS` publié à côté (`sha256sum -c SHA256SUMS`) avant de lancer quoi que ce soit.
- Installez la CLI `orkeon` depuis une source de confiance, et ne renseignez `ORKEON_CLI_DIR` que vers un dossier que vous contrôlez : Studio exécute le binaire qu'il y trouve. Sous Windows, Studio exécute aussi l'`orkeon.exe` du `libexec\orkeon` voisin du `bin\` d'un `orkeon.cmd` trouvé dans le `PATH`, et celui de `%LOCALAPPDATA%\Programs\Orkeon\libexec\orkeon` : gardez ces dossiers modifiables par vous seul.
- Gardez les secrets hors des fichiers de configuration que vous partagez. Un secret va dans une variable d'environnement, ou dans le coffre de clés que Studio propose.
- Relisez les montages et les droits d'outils qu'une équipe reçoit avant de la lancer : la configuration que Studio édite est celle que la CLI applique.
