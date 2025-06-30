import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import Home from '@/app/page'

// Mock the components to avoid dependencies
jest.mock('@/app/common/navBar', () => ({
  NavBar: () => <nav data-testid="navbar">Navigation</nav>
}))

jest.mock('@/app/components/heroSection', () => ({
  __esModule: true,
  default: () => <section data-testid="hero">Hero Section</section>
}))

jest.mock('@/app/common/footer', () => ({
  SiteFooter: () => <footer data-testid="footer">Footer</footer>
}))

jest.mock('@/app/components/teamMembers', () => ({
  TeamCarousel: () => <section data-testid="team">Team</section>
}))

jest.mock('@/app/components/contactForm', () => ({
  __esModule: true,
  default: () => <section data-testid="contact">Contact</section>
}))

jest.mock('@/app/components/avantage', () => ({
  __esModule: true,
  default: () => <section data-testid="advantages">Advantages</section>
}))

jest.mock('@/app/pages/about', () => ({
  AboutSection: () => <section data-testid="about">About</section>
}))

jest.mock('@/app/pages/services', () => ({
  ServicesSection: () => <section data-testid="services">Services</section>
}))

describe('Home Page', () => {
  it('renders the home page components', () => {
    render(<Home />)

    // Check if main components are rendered
    expect(screen.getByTestId('navbar')).toBeInTheDocument()
    expect(screen.getByTestId('hero')).toBeInTheDocument()
    expect(screen.getByTestId('about')).toBeInTheDocument()
    expect(screen.getByTestId('services')).toBeInTheDocument()
    expect(screen.getByTestId('team')).toBeInTheDocument()
    expect(screen.getByTestId('contact')).toBeInTheDocument()
    expect(screen.getByTestId('advantages')).toBeInTheDocument()
    expect(screen.getByTestId('footer')).toBeInTheDocument()
  })

  it('has proper page structure', () => {
    render(<Home />)
    
    const mainContent = screen.getByRole('main')
    expect(mainContent).toBeInTheDocument()
    expect(mainContent).toHaveClass('flex-grow', 'pt-16', 'lg:pt-20')
  })
})
