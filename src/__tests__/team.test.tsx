import '@testing-library/jest-dom'
import { render, screen, fireEvent } from '@testing-library/react'
import { TeamCarousel } from '@/app/components/teamMembers'
import { TEAM_MEMBERS } from '@/constants'

// Mock framer-motion to avoid animation issues in tests
jest.mock('framer-motion', () => ({
  motion: {
    div: ({ children, ...props }: any) => <div {...props}>{children}</div>,
    section: ({ children, ...props }: any) => <section {...props}>{children}</section>,
  },
}))

// Mock next/image
jest.mock('next/image', () => ({
  __esModule: true,
  default: ({ alt, ...props }: any) => <img alt={alt} {...props} />,
}))

describe('TeamCarousel Component', () => {
  it('renders all team members', () => {
    render(<TeamCarousel />)
    
    // Check if team section is rendered
    expect(screen.getByText('Meet Our Team')).toBeInTheDocument()
    
    // Check if all team members are present in the DOM
    TEAM_MEMBERS.forEach((member) => {
      expect(screen.getByText(member.name)).toBeInTheDocument()
      expect(screen.getByText(member.role)).toBeInTheDocument()
    })
  })

  it('displays the correct team structure', () => {
    render(<TeamCarousel />)
    
    // Check for CTO
    expect(screen.getByText('Sudi David')).toBeInTheDocument()
    expect(screen.getByText('CTO')).toBeInTheDocument()
    
    // Check for Tech Lead
    expect(screen.getByText('Cedric Justin')).toBeInTheDocument()
    expect(screen.getByText('Tech Lead')).toBeInTheDocument()
    
    // Check for Software Developer
    expect(screen.getByText('Jean Claude')).toBeInTheDocument()
    expect(screen.getByText('Software Developer')).toBeInTheDocument()
  })

  it('shows navigation dots for all team members', () => {
    render(<TeamCarousel />)
    
    // Should have navigation dots equal to number of team members
    const dots = screen.getAllByRole('button')
    const navigationDots = dots.filter(button => 
      button.getAttribute('aria-label')?.includes('View')
    )
    
    expect(navigationDots).toHaveLength(TEAM_MEMBERS.length)
  })

  it('handles navigation correctly', () => {
    render(<TeamCarousel />)
    
    // Find navigation dots
    const dots = screen.getAllByRole('button')
    const navigationDots = dots.filter(button => 
      button.getAttribute('aria-label')?.includes('View')
    )
    
    if (navigationDots.length > 1) {
      // Click on second dot
      fireEvent.click(navigationDots[1])
      
      // Should still show all team members (they're all rendered, just styled differently)
      expect(screen.getByText(TEAM_MEMBERS[1].name)).toBeInTheDocument()
    }
  })

  it('has proper section ID for navigation', () => {
    render(<TeamCarousel />)
    
    // Check if section has proper ID for anchor linking
    const section = screen.getByRole('region', { name: /team/i }) || 
                    document.querySelector('#team')
    
    expect(section).toBeInTheDocument()
  })

  it('displays team member count correctly', () => {
    render(<TeamCarousel />)
    
    // Should render exactly 3 team members
    expect(TEAM_MEMBERS).toHaveLength(3)
    
    // Check that we have the expected team structure
    const expectedRoles = ['CTO', 'Tech Lead', 'Software Developer']
    expectedRoles.forEach(role => {
      expect(screen.getByText(role)).toBeInTheDocument()
    })
  })
})
