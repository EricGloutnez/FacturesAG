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

## Données

`Documents\Factures AG\` : `factures-ag.db` (SQLite) et `Factures\` (PDF). Pour sauvegarder, copiez ce dossier.
La variable d'environnement `FACTURES_AG_DATA` permet d'utiliser un autre dossier (essais).

## Développement

```
dotnet build                                          # compiler
dotnet run --project tests/FacturesAG.Tests           # tests (sans dépendance)
dotnet publish src/FacturesAG -c Release -o publish   # produit publish/Factures AG.exe
```

- `src/FacturesAG.Core` : règles de calcul (`Billing`), lecture d'ag-connect (`AgConnectParser`), stockage (`Store`),
  facture PDF (`InvoicePdf`).
- `src/FacturesAG` : interface WPF.
- `tests/FacturesAG.Tests` : tests (données fictives).

## Licences des dépendances

- [QuestPDF](https://www.questpdf.com/license/) : licence Community, gratuite pour les particuliers et les entreprises
  dont le revenu annuel brut est inférieur à 1 M$ US.
- [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) : MIT.
