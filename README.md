# Factures AG

Facturation simple, pour un travailleur autonome, des assemblées de copropriété animées en virtuel
et listées dans [ag-connect](https://ag-connect.com). Application Windows (WPF, .NET 10),
livrée en un seul fichier `Factures AG.exe`, sans installation.

## Premier lancement

Dans **Réglages** :
1. Entrez vos coordonnées, celles du client et le prochain numéro de facture.
2. Ajoutez votre taux horaire dans la **grille de tarifs** (date d'entrée en vigueur, taux, minimum d'heures).
3. Facultatif : importez votre historique depuis Finance D (export JSON : Options → Mon dossier →
   Exporter mes enregistrements au format JSON). Le prochain numéro est ajusté automatiquement.

## Utilisation

1. **Facture en cours** : « Coller depuis ag-connect » (page *Mes invitations*, Ctrl+A puis Ctrl+C), ou « + Assemblée ».
   Saisissez l'heure de fin (`20:34`, `20h34` ou `2034`) ; le montant se calcule seul. Décochez *Quorum* si le quorum
   n'est pas atteint (minimum facturé). Tapez un montant pour le forcer, effacez-le pour revenir au calcul.
2. **Finaliser** : la facture reçoit son numéro, ses montants sont figés et le PDF est enregistré.
3. **Factures** : ouvrir le PDF, marquer payée (date et mode de paiement).
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

Le `.exe` doit se trouver dans un dossier où vous avez le droit d'écrire (Documents, Bureau…), pas dans
`Program Files`. Les mises à jour sont désactivées dans les versions de développement (Debug) ou avec la variable
d'environnement `FACTURES_AG_NO_UPDATE=1`.

Avant tout changement de structure de la base (migration), une copie est enregistrée dans
`Documents\Factures AG\Sauvegardes\`.

## Données

`Documents\Factures AG\` : `factures-ag.db` (SQLite) et `Factures\` (PDF). Pour sauvegarder, copiez ce dossier.
La variable d'environnement `FACTURES_AG_DATA` permet d'utiliser un autre dossier (essais).

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
