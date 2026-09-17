# Homepage launch video

The Trykatch marketing homepage embeds a 22-second illustrative product overview below the hero. It shows project creation, full-stack module generation, and a sample CRUD interaction; it is not a live recording or a speed benchmark. The original animation is maintained separately from the website delivery assets.

The player uses native controls, inline mobile playback, a settled poster, a fixed 16:9 layout and `preload="none"`. Playback and sound require user interaction. An expandable text description and the CLI commands provide an alternative to the visual-only demonstration. Surrounding copy is available in English and French; the video itself is English, without narration.

## Run locally

From the repository root:

```bash
cd templates/trykatch/web
corepack pnpm install --frozen-lockfile
corepack pnpm generate
VITE_TRYKATCH_LAUNCH_VIDEO=true corepack pnpm dev
```

Open the Vite URL at `/`. No backend is needed to view this marketing page. Vite enables the section only when the marketing MP4 exists; the unchanged Vercel build discovers the same asset. Set `VITE_TRYKATCH_LAUNCH_VIDEO=false` to hide it. The marketing assets are excluded from both NuGet packing and `dotnet new` generation, so generated applications do not display the promotional section, even if a developer sets the flag to `true` without supplying media.

## Delivery assets

- `templates/trykatch/web/apps/web/public/marketing/trykatch-launch.mp4`: H.264/AAC, 1920×1080, 30 fps, fast-start MP4.
- `templates/trykatch/web/apps/web/public/marketing/trykatch-launch.webp`: the settled hook frame, not a blank fade.

Do not autoplay, load a third-party embedded player, or remove the accessible description when replacing this video. Check playback and layout at 320, 768, and 1440 px. Preserve the audio attribution in the player description and asset credits.

Music: [Happy Beats & Business Moves Vol. 12 by Sascha Ende](https://ende.app/en/song/12881-happy-beats-business-moves-vol-12), [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), edited excerpt. Interface sounds: Kenney, CC0.

## Verification record

- Production web build, TypeScript check, client generation, frontend tests, and accessibility assertions passed.
- Browser checks confirmed a paused poster and zero MP4 resource requests before play; H.264/AAC playback and seeking to the record scene worked without a media error.
- The 320 px phone layout had a 288 px player and no document overflow. The 768 px French/dark layout and 1440 px desktop layout were also inspected.
- The web MP4 is 910,015 bytes, 1920×1080, 30 fps, 660 video frames. Its container duration is 22.016 seconds due to AAC padding. The WebP poster is about 22 KB.
- Local NuGet packing and generation confirmed marketing media is absent from the package and generated product. The fixture intentionally disallowed script post-actions; its Git initialization was not executed.
- Public deployment is pending the normal PR promotion workflow. The local marketing preview is at http://localhost:5186/ while that development server is running.
