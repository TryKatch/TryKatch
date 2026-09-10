---
title: Installer et créer une application
description: Installez le modèle Trykatch et générez une solution .NET et React complète.
---

## Installer le modèle

Installez une seule fois la CLI Trykatch, puis laissez-la installer le modèle de projet .NET et React correspondant avec une progression visible :

```bash
dotnet tool install --global Trykatch.Cli --version 0.1.0-preview.5
trykatch template install
```

L’installateur utilise le moteur de modèles officiel de Microsoft. Dans un terminal interactif, il affiche une progression animée ; dans la CI, il produit des lignes de journal stables. La commande directe reste disponible pour l’automatisation :

```bash
dotnet new install Trykatch.Templates@0.1.0-preview.5
```

Générez un backend .NET 10 et un espace de travail React complets avec le nom de votre produit :

```bash
dotnet new trykatch -n Horizon
cd Horizon
```

Les noms contenant des points ou des tirets sont normalisés pour produire des espaces de noms C#, des répertoires, des noms de conteneurs et des packages npm valides.

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
trykatch template help
trykatch module help
```

`trykatch help` présente les choix de génération d’application. `trykatch template help` documente l’installation et la mise à jour du modèle. `trykatch module help` répertorie toutes les commandes prises en charge pour le cycle de vie des modules.

## Installation dans l’IDE

Le package utilise le moteur de modèles standard de .NET. Rider peut installer le fichier `.nupkg` depuis **New Solution → More Templates → Install Template**. Après l’installation, Visual Studio découvre les modèles du SDK dans **Create a new project** ; recherchez **Trykatch**.

:::caution[État de préversion]
Trykatch est une préversion orientée production, pas une certification de mise en production. Chaque produit généré doit effectuer sa propre qualification de sécurité, de restauration, de capacité et d’exploitation.
:::
