# Reproductibilité des dépendances

Exécutez les commandes depuis la racine de l’application générée. `global.json` impose la bande de fonctionnalités .NET 11.0.1xx testée, à partir de 11.0.100, et autorise uniquement les mises à jour correctives. Installez cette bande même si un autre SDK .NET est déjà présent. Qualifiez ensemble toute modification de cette politique et du SDK sélectionné dans les workflows.

Les actions tierces des workflows sont épinglées par commit. La configuration React épingle aussi pnpm dans ses manifestes racine et web ; utilisez Corepack. La configuration backend seul ne contient volontairement ni manifeste ni commandes frontend.

Les fichiers de verrouillage NuGet du modèle sont exclus : les références aux projets sont renommées à la génération. Initialisez-les avec :

```bash
dotnet restore Trykatch.slnx
dotnet build Trykatch.slnx --no-restore
```

Vérifiez puis versionnez les fichiers `packages.lock.json` générés avec le code. La première CI ne doit pas dépendre d’un cache indexé sur des fichiers inexistants ; les workflows livrés n’activent donc pas le cache NuGet. React peut utiliser immédiatement le fichier livré `web/pnpm-lock.yaml`.

Pour le graphe versionné d’un projet d’exécution, une restauration verrouillée refuse une modification non examinée des dépendances :

```bash
dotnet restore src/API/Trykatch.Api/Trykatch.Api.csproj --locked-mode
```

La CI du dépôt restaure la solution en mode verrouillé. La première restauration de l’application générée crée les fichiers de verrouillage sous les nouveaux noms. Après les avoir versionnés et avoir validé vos runners de déploiement, utilisez `dotnet restore Trykatch.slnx --locked-mode` dans les exécutions suivantes. Aspire 17 fournit son tableau de bord via son outil CLI.

Après une modification examinée d’un package ou module, régénérez les verrous concernés par une restauration normale, examinez les différences, compilez et relancez les tests PostgreSQL et de contrats appropriés avant de versionner. Une mise à jour du package de modèle ne modifie pas automatiquement les produits déjà générés.
