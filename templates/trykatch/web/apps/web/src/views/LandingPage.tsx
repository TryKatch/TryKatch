import { applicationName } from '../branding'
import { ArrowRight, Boxes, Database, LockKeyhole, Waypoints } from 'lucide-react'
import { LandingPreferencesMenu } from '../components/LandingPreferencesMenu'
import { LaunchVideo } from '../components/LaunchVideo'
import { ProductLogo } from '../components/ProductLogo'
import { useI18n } from '../i18n/I18nProvider'

const foundations = [
  {
    icon: LockKeyhole,
    title: 'Security kernel',
    description: 'Identity, organization resolution, PostgreSQL RLS, permissions, audit, and the transactional outbox stay centralized and difficult to bypass.',
  },
  {
    icon: Boxes,
    title: 'Composable modules',
    description: 'Business capabilities contribute APIs, data models, permissions, React routes, events, and workers through explicit, validated contracts.',
  },
  {
    icon: Waypoints,
    title: 'Operational foundation',
    description: 'PostgreSQL, OpenTelemetry, health checks, containers, and Grafana-compatible telemetry provide a visible path from development to operation.',
  },
] as const

export function LandingPage() {
  const { t } = useI18n()
  return <main className="landing-page">
    <header className="landing-nav">
      <a className="landing-brand" href="/" aria-label={`${applicationName} home`}>
        <span className="brand-mark"><ProductLogo size={18} /></span>
        <strong>{applicationName}</strong>
      </a>
      <nav className="landing-nav-actions" aria-label="Product links">
        <a className="landing-nav-link" href="https://docs.trykatch.net">{t('Documentation')}</a>
        <a className="landing-nav-link" href="https://github.com/TryKatch/TryKatch">GitHub</a>
        <LandingPreferencesMenu />
        <a className="landing-sign-in" href="#install">{t('Get started')} <ArrowRight size={15} /></a>
      </nav>
    </header>

    <section className="landing-hero" aria-labelledby="landing-title">
      <div>
        <span className="landing-kicker">{t('Enterprise application foundation')}</span>
        <h1 id="landing-title">{t('Build multi-tenant apps with .NET and React')}</h1>
        <p>{t('Trykatch gives .NET teams a secure multi-tenant foundation with an owned React experience, so every new product does not begin by rebuilding identity, isolation, permissions, and operations.')}</p>
        <div className="landing-actions">
          <a className="landing-primary-action" href="#install">{t('Get started')} <ArrowRight size={16} /></a>
          <a className="landing-secondary-action" href="https://github.com/TryKatch/TryKatch" aria-label={t('View source')}>
            <svg width="20" height="20" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
              <path d="M10.226 17.284c-2.965-.36-5.054-2.493-5.054-5.256 0-1.123.404-2.336 1.078-3.144-.292-.741-.247-2.314.09-2.965.898-.112 2.111.36 2.83 1.01.853-.269 1.752-.404 2.853-.404 1.1 0 1.999.135 2.807.382.696-.629 1.932-1.1 2.83-.988.315.606.36 2.179.067 2.942.72.854 1.101 2 1.101 3.167 0 2.763-2.089 4.852-5.098 5.234.763.494 1.28 1.572 1.28 2.807v2.336c0 .674.561 1.056 1.235.786 4.066-1.55 7.255-5.615 7.255-10.646C23.5 6.188 18.334 1 11.978 1 5.62 1 .5 6.188.5 12.545c0 4.986 3.167 9.12 7.435 10.669.606.225 1.19-.18 1.19-.786V20.63a2.9 2.9 0 0 1-1.078.224c-1.483 0-2.359-.808-2.987-2.313-.247-.607-.517-.966-1.034-1.033-.27-.023-.359-.135-.359-.27 0-.27.45-.471.898-.471.652 0 1.213.404 1.797 1.235.45.651.921.943 1.483.943.561 0 .92-.202 1.437-.719.382-.381.674-.718.944-.943" />
            </svg>
          </a>
        </div>
      </div>
      <aside className="landing-principle" aria-label="Trykatch design principle">
        <ProductLogo size={38} />
        <p>{t('Start secure.')}<br />{t('Build freely.')}</p>
        <small>{t('One security boundary. Explicit modules. Operations designed in.')}</small>
      </aside>
    </section>

    {import.meta.env.VITE_TRYKATCH_LAUNCH_VIDEO === 'true' && <LaunchVideo />}

    <section className="landing-problem" aria-labelledby="problem-title">
      <span className="landing-section-number">01</span>
      <div>
        <h2 id="problem-title">{t('The problem')}</h2>
        <p>{t('Teams repeatedly spend their earliest product cycles assembling the same risky foundation: authentication, tenant boundaries, authorization, auditability, deployment, and observability. Those pieces often grow apart and become expensive to secure.')}</p>
      </div>
      <p className="landing-problem-answer">{t('Trykatch makes those concerns a coherent platform kernel, leaving product teams to focus on the domain that makes their application valuable.')}</p>
    </section>

    <section className="landing-architecture" id="architecture" aria-labelledby="architecture-title">
      <div className="landing-section-heading">
        <span className="landing-section-number">02</span>
        <div><h2 id="architecture-title">{t('Structured for change')}</h2><p>{t('Stable foundations below. Replaceable business capabilities above.')}</p></div>
      </div>
      <div className="landing-foundations">
        {foundations.map(({ icon: Icon, title, description }) => <article key={title}>
          <span><Icon size={18} /></span>
          <h3>{t(title)}</h3>
          <p>{t(description)}</p>
        </article>)}
      </div>
      <div className="landing-flow" aria-label="Trykatch architecture layers">
        <span><strong>{t('Experience')}</strong><small>{t('React · TanStack · owned components')}</small></span>
        <ArrowRight size={16} aria-hidden="true" />
        <span><strong>{t('Modules')}</strong><small>{t('API · data · permissions · events')}</small></span>
        <ArrowRight size={16} aria-hidden="true" />
        <span><strong>{t('Kernel')}</strong><small>{t('Identity · RLS · audit · operations')}</small></span>
      </div>
    </section>

    <section className="landing-install" id="install" aria-labelledby="install-title">
      <div className="landing-section-heading">
        <span className="landing-section-number">03</span>
        <div><h2 id="install-title">{t('Create your application')}</h2><p>{t('Install once, then generate a complete backend and React workspace with your own product name.')}</p></div>
      </div>
      <div className="landing-command" aria-label="Trykatch installation commands">
        <code><span>$</span> dotnet new install Trykatch.Templates@0.1.0-preview.33</code>
        <code><span>$</span> dotnet new trykatch -n Horizon</code>
      </div>
      <p className="landing-open-source">{t('Open source under Apache-2.0. Use it, extend it, and ship products on top of it.')}</p>
    </section>

    <section className="landing-production" aria-labelledby="production-title">
      <div className="landing-production-icon"><Database size={20} /></div>
      <div>
        <span className="landing-section-number">04</span>
        <h2 id="production-title">{t('Designed for production qualification')}</h2>
        <p>{t('Trykatch provides the architecture, automated checks, and deployment foundations a serious workload needs. Each generated product must still prove its own security, recovery, capacity, and operational requirements before release.')}</p>
      </div>
      <a href="https://docs.trykatch.net">{t('Read the documentation')} <ArrowRight size={15} /></a>
    </section>

    <footer className="landing-footer"><span>{applicationName}</span><small>Open source · Apache-2.0 · .NET 10 · React · PostgreSQL</small></footer>
  </main>
}
