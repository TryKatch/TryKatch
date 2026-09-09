import { ArrowRight, Boxes, Database, LockKeyhole, Waypoints } from 'lucide-react'
import { TrykatchLogo } from '../components/TrykatchLogo'
import { LanguageSwitcher } from '../i18n/LanguageSwitcher'
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
      <a className="landing-brand" href="/" aria-label="Trykatch home">
        <span className="brand-mark"><TrykatchLogo size={18} /></span>
        <strong>Trykatch</strong>
      </a>
      <nav className="landing-nav-actions" aria-label="Product links">
        <a className="landing-nav-link" href="https://docs.trykatch.net">{t('Documentation')}</a>
        <a className="landing-nav-link" href="https://github.com/TryKatch/TryKatch">GitHub</a>
        <LanguageSwitcher compact />
        <a className="landing-sign-in" href="#install">{t('Get started')} <ArrowRight size={15} /></a>
      </nav>
    </header>

    <section className="landing-hero" aria-labelledby="landing-title">
      <div>
        <span className="landing-kicker">{t('Enterprise application foundation')}</span>
        <h1 id="landing-title">{t('Ship the product.')}<br />{t('Keep the hard parts dependable.')}</h1>
        <p>{t('Trykatch gives .NET teams a secure multi-tenant foundation with an owned React experience, so every new product does not begin by rebuilding identity, isolation, permissions, and operations.')}</p>
        <div className="landing-actions">
          <a className="landing-primary-action" href="https://github.com/TryKatch/TryKatch">{t('View source')} <ArrowRight size={16} /></a>
          <a className="landing-secondary-action" href="#architecture">{t('See how it is structured')}</a>
        </div>
      </div>
      <aside className="landing-principle" aria-label="Trykatch design principle">
        <TrykatchLogo size={38} />
        <p>{t('Start secure.')}<br />{t('Build freely.')}</p>
        <small>{t('One security boundary. Explicit modules. Operations designed in.')}</small>
      </aside>
    </section>

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
        <code><span>$</span> dotnet new install Trykatch.Templates</code>
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

    <footer className="landing-footer"><span>Trykatch</span><small>Open source · Apache-2.0 · .NET 10 · React · PostgreSQL</small></footer>
  </main>
}
