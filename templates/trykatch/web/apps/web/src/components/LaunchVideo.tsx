import { useI18n } from '../i18n/I18nProvider'

const videoPath = '/marketing/trykatch-launch.mp4'

export function LaunchVideo() {
  const { t } = useI18n()
  return <section className="landing-demo" aria-labelledby="launch-video-title">
    <div className="landing-demo-heading">
      <div>
        <span className="landing-kicker">{t('One module. Both sides.')}</span>
        <h2 id="launch-video-title">{t('See what you can build')}</h2>
      </div>
      <span className="landing-demo-duration">{t('22-second overview')}</span>
    </div>
    <figure>
      <video controls playsInline preload="none" poster="/marketing/trykatch-launch.webp"
        width={1920} height={1080} aria-label={t('Trykatch product overview')}
        aria-describedby="launch-video-description">
        <source src={videoPath} type="video/mp4" />
        <a href={videoPath}>{t('Download the video')}</a>
      </video>
      <figcaption id="launch-video-description">
        {t('An illustrative workflow: generate a full-stack module, then create a record. Not a runtime benchmark.')}
      </figcaption>
    </figure>
    <details className="landing-demo-details">
      <summary>{t('Video description and credits')}</summary>
      <p>{t('The video shows a Horizon application with an Invoicing module. The CLI creates its Domain, Application, IntegrationEvents, Presentation, Infrastructure, and Web layers. A sample interface then creates a record named September retainer with a description. Music and interface sounds accompany the demonstration; there is no narration.')}</p>
      <pre><code>{`trykatch new Horizon
cd Horizon
trykatch module create Invoicing \\
  --entity Invoice \\
  --resource invoices \\
  --ownership organization \\
  --with-web`}</code></pre>
      <p>{t('Music')}: <a href="https://ende.app/en/song/12881-happy-beats-business-moves-vol-12">Happy Beats &amp; Business Moves Vol. 12 — Sascha Ende</a>. <a href="https://creativecommons.org/licenses/by/4.0/">CC BY 4.0</a>. {t('Edited excerpt.')}</p>
      <a href={videoPath} download>{t('Download the video')}</a>
    </details>
  </section>
}
