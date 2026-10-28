# Factures AG

Facturation simple, pour un travailleur autonome, des assemblées de copropriété animées en virtuel
et listées dans [ag-connect](https://ag-connect.com). Application Windows (WPF, .NET 10),
livrée en un seul fichier `.exe`.

## Installation

Téléchargez `FacturesAG-x.y.z-win-x64.exe` depuis les [Releases](https://github.com/EricGloutnez/FacturesAG/releases)
et lancez-le. Au premier lancement, l'application **s'installe d'elle-même** (sans droits d'administrateur)
dans `%LOCALAPPDATA%\Programs\FacturesAG` et crée un raccourci **« Factures AG »** sur le Bureau et dans le menu
Démarrer. Le fichier téléchargé peut ensuite être supprimé. Une version déjà installée plus récente n'est jamais
remplacée par un vieux téléchargement.

Pour désinstaller : supprimez le dossier `%LOCALAPPDATA%\Programs\FacturesAG` et les deux raccourcis
(vos données restent dans `%LOCALAPPDATA%\Factures AG`).

> Au premier lancement, Windows SmartScreen peut afficher « Windows a protégé votre ordinateur » (fichier non signé) :
> cliquez sur **Informations complémentaires → Exécuter quand même**.

## Premier lancement

Dans **Réglages** :
1. Entrez vos coordonnées, celles du client, le délai de paiement par défaut, le prochain numéro de facture.
2. Ajoutez votre taux horaire dans la **grille de tarifs** (date d'entrée en vigueur, taux, minimum d'heures).
3. Facultatif : importez votre historique depuis Finance D (export JSON : Options → Mon dossier →
   Exporter mes enregistrements au format JSON). Le prochain numéro est ajusté automatiquement.

## Utilisation

1. **Facture en cours** : « Coller depuis ag-connect » (page *Mes invitations*, Ctrl+A puis Ctrl+C), ou « + Assemblée ».
   Saisissez l'heure de fin (`20:34`, `20h34` ou `2034`) ; le montant se calcule seul. Décochez *Quorum* si le quorum
   n'est pas atteint (minimum facturé). Tapez un montant pour le forcer, effacez-le pour revenir au calcul.
   « + Ligne libre » ajoute une ligne à montant fixe (formation, per diem, kilométrage…).
2. **Finaliser** : choisissez la date et le délai de paiement (Net 0, Net 15 ou Net 30) ; la facture reçoit son numéro,
   ses montants sont figés et le PDF est enregistré (avec le logo Factures AG et la date d'échéance).
3. **Factures** : ouvrir le PDF, marquer payée (date et mode de paiement), voir l'échéance (« En retard » si dépassée).
   **Rouvrir** permet de modifier une facture finalisée, y compris une facture importée : son numéro ne change pas,
   la facture du mois est mise de côté le temps de la modification, « Annuler les modifications » la remet exactement
   comme avant, et l'ancien PDF est gardé dans `Factures\Versions remplacées\`. Si une facture payée change de total,
   elle repasse « À payer ».
4. **État des résultats** : revenus de l'année par catégorie, reste à encaisser.

## Règle de calcul (assemblée virtuelle)

Taux horaire en vigueur à la date de l'assemblée × heures commencées, avec un minimum d'heures
(par exemple 30 $/h, minimum 2 h : 2 h 04 → 3 h → 90 $). Les assemblées qui finissent après minuit sont gérées.
Les factures finalisées ne changent jamais, même si la grille est modifiée ensuite.
Pour l'instant, les assemblées hybrides (avec adresse) sont ignorées lors du collage.

## Mises à jour (automatiques et obligatoires)

Au démarrage, puis toutes les 4 heures si l'application reste ouverte, Factures AG consulte la dernière version
publiée sur GitHub. Si elle est plus récente, elle est téléchargée, vérifiée (taille et empreinte SHA-256 fournie par
GitHub), installée à la place du `.exe`, puis l'application redémarre. Il n'est pas possible de la reporter : en cas
d'échec, seuls « Réessayer » et « Quitter » sont proposés. Sans Internet, l'application s'ouvre normalement.

L'application installée se trouve dans un dossier où elle a le droit d'écrire, ce qui permet ces mises à jour.
Les mises à jour sont désactivées dans les versions de développement (Debug) ou avec la variable
d'environnement `FACTURES_AG_NO_UPDATE=1` ; l'installation, avec `FACTURES_AG_NO_INSTALL=1`.

Avant tout changement de structure de la base (migration), une copie est enregistrée dans
`%LOCALAPPDATA%\Factures AG\Sauvegardes\`.

## Données

`%LOCALAPPDATA%\Factures AG\` (soit `C:\Users\<vous>\AppData\Local\Factures AG`) : `factures-ag.db` (SQLite),
`Factures\` (PDF) et `Sauvegardes\`. Pour sauvegarder, copiez ce dossier (bouton « Ouvrir le dossier » dans Réglages).

Ce dossier est utilisé plutôt que Documents parce que la protection « Accès contrôlé aux dossiers » de Windows
Defender bloque les programmes inconnus qui écrivent dans Documents ou sur le Bureau.

### Changer de dossier

Réglages → Vos données → **Changer de dossier…** : vos données (base, PDF, sauvegardes) sont **copiées** dans le dossier
choisi (rien n'est supprimé à l'ancien endroit), puis l'application redémarre sur ce dossier. Le choix est noté dans
`%LOCALAPPDATA%\Factures AG\emplacement-donnees.txt`. Un dossier où Windows interdit d'écrire (Documents ou Bureau
protégés par Defender) est refusé. Si le dossier choisi contient déjà des données, vous choisissez lesquelles garder
(l'autre base est mise de côté). Si le dossier choisi devient introuvable (disque débranché, iCloud/OneDrive
indisponible), l'application refuse de s'ouvrir plutôt que de démarrer sur une base vide. Dossier synchronisé en
ligne : n'ouvrir Factures AG que sur un seul ordinateur à la fois.

### Protections

- **Sauvegardes automatiques** dans `Sauvegardes\` : une par jour (les 30 derniers jours), une avant chaque import
  Finance D et une avant chaque changement de structure de la base.
- **Réglages** : enregistrés tout ou rien ; effacer un champ rempli demande confirmation ; quitter l'onglet avec des
  modifications non enregistrées propose de les enregistrer.
- **Une seule copie ouverte à la fois** : relancer l'application ramène la fenêtre déjà ouverte.
- **Copie privée d'une autre application** : lancée depuis une application « empaquetée », Windows peut rediriger
  `AppData` vers une copie séparée (vide). Factures AG le détecte (emplacement réel du dossier) et refuse de démarrer.
- **Mises à jour** : jamais au milieu d'une saisie (bandeau « Mettre à jour maintenant », sinon installation après
  2 minutes sans saisie) ; toujours obligatoires au démarrage. Seul le `.exe` officiel (GitHub Actions,
  `-p:OfficialBuild=true`) s'installe et se met à jour.

### Essais

`FACTURES_AG_DATA` désigne un autre dossier de données : la fenêtre affiche alors un bandeau rouge
« DONNÉES D'ESSAI ». `FACTURES_AG_INSTALL_DIR` et `FACTURES_AG_SHORTCUT_DIR` redirigent l'installation et les raccourcis ;
`FACTURES_AG_NO_UPDATE=1` et `FACTURES_AG_NO_INSTALL=1` désactivent mise à jour et installation.

## Développement

```
dotnet build                                          # compiler
dotnet run --project tests/FacturesAG.Tests           # tests (sans dépendance)
dotnet publish src/FacturesAG -c Release -o publish   # produit publish/Factures AG.exe
```

- `src/FacturesAG.Core` : règles de calcul (`Billing`), lecture d'ag-connect (`AgConnectParser`), stockage et
  migrations (`Store`), facture PDF (`InvoicePdf`), mises à jour (`Updater`).
- `src/FacturesAG` : interface WPF (`UpdateFlow` pour la mise à jour obligatoire).

### Publier une version

```
git tag -a v0.3.0 -m "Notes de version (affichées sur GitHub)"
git push origin v0.3.0
```

Le flux GitHub Actions `.github/workflows/publier.yml` lance les tests, compile le `.exe` avec ce numéro et crée la
version GitHub ; toutes les applications installées s'y mettent à jour. Si un test échoue, rien n'est publié.

### Changer la structure de la base

Ajouter une entrée à la fin de `Store.Migrations` (numéro suivant, SQL à appliquer). Ne jamais modifier une
migration déjà publiée.
- `tests/FacturesAG.Tests` : tests (données fictives).

## Licences des dépendances

- [QuestPDF](https://www.questpdf.com/license/) : licence Community, gratuite pour les particuliers et les entreprises
  dont le revenu annuel brut est inférieur à 1 M$ US.
- [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) : MIT.

## Logo

Le logo « facture et horloge » est intégré à l'application (en-tête des factures, fenêtre et icône du `.exe`) et
n'est pas modifiable depuis l'application. Source : `assets/logo-icone.svg` ; l'icône Windows
`src/FacturesAG/Assets/app.ico` se régénère avec `assets/generer-icone.ps1`.
