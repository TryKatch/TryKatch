---
title: Installer et créer une application
description: Installez le modèle Trykatch et générez une solution .NET et React complète.
---

## Installer le modèle

Suivez ces étapes dans l’ordre.

### 1. Installer la CLI Trykatch

Exécutez cette commande une seule fois sur votre ordinateur :

```bash
dotnet tool install --global Trykatch.Cli --version 0.1.0-preview.7
```

Cette commande installe la commande `trykatch`. Elle n’installe pas encore le modèle de projet.

### 2. Installer le modèle de projet

Lorsque l’étape 1 a réussi, exécutez :

```bash
trykatch template install
```

Cette commande installe le modèle .NET et React correspondant. Elle utilise le moteur de modèles officiel de Microsoft, affiche une progression animée dans un terminal interactif et produit des lignes de journal stables dans la CI.

### 3. Créer votre application

Remplacez `Horizon` par le nom de votre produit :

```bash
dotnet new trykatch -n Horizon
cd Horizon
```

:::note[Alternative : installer sans la CLI Trykatch]
Si vous ne souhaitez pas utiliser la CLI Trykatch avec progression, utilisez la commande suivante **à la place des étapes 1 et 2** :

```bash
dotnet new install Trykatch.Templates@0.1.0-preview.7
```
:::

Les noms contenant des points ou des tirets sont normalisés pour produire des espaces de noms C#, des répertoires, des noms de conteneurs et des packages npm valides.

## Mettre à jour le modèle

Mettez d’abord à jour la CLI, puis utilisez la commande de mise à jour dédiée de Trykatch. La CLI et le modèle restent ainsi sur la même version :

```bash
dotnet tool update --global Trykatch.Cli --version 0.1.0-preview.7
trykatch update
```

`trykatch template update` est l’équivalent explicite. Ajoutez `--version <version>` à l’une ou l’autre commande pour sélectionner une version précise.

## Choisir l’interface générée

React est inclus par défaut. Ne générez une solution limitée au backend que lorsque ce choix est intentionnel :

```bash
dotnet new trykatch -n Horizon --ui none
```

Les modules gratuits optionnels sont désactivés par défaut :

```bash
dotnet new trykatch -n Horizon \
  --email true \
  --storage true \
  --documents true \
  --images true
```

## Découvrir les commandes disponibles

Utilisez les commandes d’aide ciblées de la CLI :

```bash
trykatch help
trykatch update
trykatch template help
trykatch module help
```

`trykatch help` présente les choix de génération d’application. `trykatch template help` documente l’installation et la mise à jour du modèle. `trykatch module help` répertorie toutes les commandes prises en charge pour le cycle de vie des modules.

## Démarrer dans Rider

Ouvrez le fichier `.slnx` généré, laissez Rider restaurer la solution, puis sélectionnez **`<NomApplication>.AppHost: https`** comme configuration d’exécution. Le profil AppHost démarre PostgreSQL, exécute le migrateur, injecte les identifiants distincts des rôles d’exécution de la base de données, puis démarre l’API et l’application React. Gardez Docker actif et n’utilisez pas le projet API seul comme projet de démarrage.

Dans le terminal de Rider, la commande équivalente est :

```bash
dotnet run --launch-profile https --project src/Horizon.AppHost/Horizon.AppHost.csproj
```

## Installation du modèle dans l’IDE

Le package utilise le moteur de modèles standard de .NET. Rider peut installer le fichier `.nupkg` depuis **New Solution → More Templates → Install Template**. Après l’installation, Visual Studio découvre les modèles du SDK dans **Create a new project** ; recherchez **Trykatch**.

:::caution[État de préversion]
Trykatch est une préversion orientée production, pas une certification de mise en production. Chaque produit généré doit effectuer sa propre qualification de sécurité, de restauration, de capacité et d’exploitation.
:::
