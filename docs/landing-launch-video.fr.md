# Vidéo de la page d’accueil

La page publique de Trykatch présente une vidéo illustrative de 22 secondes sous la section principale. Elle montre la génération d’un module full-stack, puis la création d’un enregistrement. Ce n’est ni un enregistrement en direct ni un test de performance.

Le lecteur conserve l’identité visuelle, utilise une affiche 16:9 et les contrôles natifs, et ne lance jamais automatiquement la lecture ou le son. `preload="none"` évite le téléchargement de la vidéo avant l’interaction. Une description textuelle, les commandes CLI et les crédits sont disponibles en français et en anglais. La vidéo est en anglais, sans narration.

## Vérifier localement

Depuis la racine du dépôt :

```bash
cd templates/trykatch/web
corepack pnpm install --frozen-lockfile
corepack pnpm generate
corepack pnpm dev
```

Ouvrez l’URL Vite à `/` ; aucun backend n’est nécessaire pour cette page. Vite affiche la vidéo uniquement si le fichier `public/marketing/trykatch-launch.mp4` existe. `VITE_TRYKATCH_LAUNCH_VIDEO=false` masque la section. Les médias promotionnels sont exclus du paquet NuGet et de `dotnet new` : les produits générés n’héritent pas de cette vidéo.

Avant publication, vérifiez la lecture et la mise en page à 320, 768 et 1440 px. Les fichiers livrés sont un MP4 H.264/AAC 1920×1080 à 30 images/s et une affiche WebP. Consultez le [guide technique](landing-launch-video.md) pour leur emplacement exact.

Musique : [Happy Beats & Business Moves Vol. 12 — Sascha Ende](https://ende.app/en/song/12881-happy-beats-business-moves-vol-12), [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), extrait monté. Sons d’interface : Kenney, CC0.
