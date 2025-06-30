// Application constants

export const SITE_CONFIG = {
  name: 'TryKatch',
  title: 'TryKatch - Professional Software Development Services',
  description: 'Transform your ideas into powerful digital solutions. We deliver high-quality, scalable software development, UI/UX design, and IT consulting services.',
  url: process.env.NEXT_PUBLIC_APP_URL || 'https://trykatch.com',
  ogImage: '/trykatch-hero.png',
  creator: 'TryKatch Team',
} as const;

export const NAVIGATION_ITEMS = [
  { href: '/', label: 'Home' },
  { href: '#about', label: 'About' },
  { href: '#services', label: 'Services' },
  { href: '#team', label: 'Team' },
  { href: '/blog', label: 'Blog' },
  { href: '#contact', label: 'Contact' },
] as const;

export const SOCIAL_LINKS = [
  {
    name: 'GitHub',
    href: process.env.NEXT_PUBLIC_GITHUB_URL || '#',
    icon: 'Github',
  },
  {
    name: 'LinkedIn',
    href: process.env.NEXT_PUBLIC_LINKEDIN_URL || '#',
    icon: 'Linkedin',
  },
  {
    name: 'Twitter',
    href: process.env.NEXT_PUBLIC_TWITTER_URL || '#',
    icon: 'Twitter',
  },
] as const;

export const CONTACT_INFO = {
  email: process.env.CONTACT_EMAIL || 'contact@trykatch.com',
  phone: '+1 (555) 123-4567',
  address: '123 Business Street, Suite 100, City, State 12345',
} as const;

export const SERVICES = [
  {
    id: 'web-development',
    title: 'Web Development',
    description: 'Modern, responsive websites and web applications built with cutting-edge technologies.',
    icon: 'Code',
    features: ['React/Next.js', 'TypeScript', 'Responsive Design', 'Performance Optimization'],
  },
  {
    id: 'mobile-development',
    title: 'Mobile Development',
    description: 'Native and cross-platform mobile applications for iOS and Android.',
    icon: 'Smartphone',
    features: ['React Native', 'Flutter', 'Native iOS/Android', 'Cross-platform'],
  },
  {
    id: 'ui-ux-design',
    title: 'UI/UX Design',
    description: 'User-centered design solutions that create engaging and intuitive experiences.',
    icon: 'Palette',
    features: ['User Research', 'Wireframing', 'Prototyping', 'Design Systems'],
  },
  {
    id: 'consulting',
    title: 'IT Consulting',
    description: 'Strategic technology consulting to help businesses make informed decisions.',
    icon: 'Users',
    features: ['Technology Strategy', 'Architecture Review', 'Performance Audit', 'Team Training'],
  },
] as const;

export const ADVANTAGES = [
  {
    id: 'experienced-team',
    title: 'Experienced Team',
    description: 'Our team of experts brings years of industry experience to every project.',
    icon: 'Award',
  },
  {
    id: 'cutting-edge-tech',
    title: 'Cutting-Edge Technology',
    description: 'We use the latest technologies and best practices to deliver modern solutions.',
    icon: 'Zap',
  },
  {
    id: 'agile-methodology',
    title: 'Agile Methodology',
    description: 'Fast, iterative development process that ensures quick delivery and flexibility.',
    icon: 'Repeat',
  },
  {
    id: 'quality-assurance',
    title: 'Quality Assurance',
    description: 'Rigorous testing and quality control to ensure bug-free, reliable software.',
    icon: 'Shield',
  },
] as const;

export const ANIMATION_VARIANTS = {
  fadeIn: {
    hidden: { opacity: 0, y: 20 },
    visible: { opacity: 1, y: 0 },
  },
  slideIn: {
    hidden: { opacity: 0, x: -20 },
    visible: { opacity: 1, x: 0 },
  },
  scaleIn: {
    hidden: { opacity: 0, scale: 0.95 },
    visible: { opacity: 1, scale: 1 },
  },
} as const;

export const API_ENDPOINTS = {
  contact: '/api/contact',
  newsletter: '/api/newsletter',
  blog: '/api/blog',
} as const;
