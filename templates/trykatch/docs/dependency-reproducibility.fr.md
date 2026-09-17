# Reproductibilité des dépendances

Exécutez les commandes depuis la racine de l’application générée. `global.json` impose la bande de fonctionnalités .NET 10.0.3xx testée, à partir de 10.0.301, et autorise uniquement les mises à jour correctives. Installez cette bande même si un autre SDK .NET est déjà présent. Qualifiez ensemble toute modification de cette politique et du SDK sélectionné dans les workflows.

Les actions tierces des workflows sont épinglées par commit. La configuration React épingle aussi pnpm dans ses manifestes racine et web ; utilisez Corepack. La configuration backend seul ne contient volontairement ni manifeste ni commandes frontend.

Les fichiers de verrouillage NuGet du modèle sont exclus : leurs noms de projets et les dépendances AppHost liées à la plateforme ne représentent pas le graphe de l’application générée. Initialisez-les avec :

```bash
dotnet restore Trykatch.slnx
dotnet build Trykatch.slnx --no-restore
```

Vérifiez puis versionnez les fichiers `packages.lock.json` générés avec le code. La première CI ne doit pas dépendre d’un cache indexé sur des fichiers inexistants ; les workflows livrés n’activent donc pas le cache NuGet. React peut utiliser immédiatement le fichier livré `web/pnpm-lock.yaml`.

Pour le graphe versionné d’un projet d’exécution, une restauration verrouillée refuse une modification non examinée des dépendances :

```bash
dotnet restore src/API/Trykatch.Api/Trykatch.Api.csproj --locked-mode
```

Testez cette commande sur le runner pris en charge avant de l’imposer. Aspire AppHost possède une dépendance de tableau de bord liée au SDK, au runtime et à la plateforme ; son verrouillage exige une qualification distincte. La restauration de toute la solution livrée n’est pas présentée comme imposant le mode verrouillé après la génération initiale. Cette transition, la première CI sur Windows/macOS/Linux et la qualification manuelle des IDE restent des critères ouverts. Ne désactivez pas silencieusement les contrôles verrouillés des projets d’exécution pour contourner une différence AppHost.

Après une modification examinée d’un package ou module, régénérez les verrous concernés par une restauration normale, examinez les différences, compilez et relancez les tests PostgreSQL et de contrats appropriés avant de versionner. Une mise à jour du package de modèle ne modifie pas automatiquement les produits déjà générés.
